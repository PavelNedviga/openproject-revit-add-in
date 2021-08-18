﻿using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenProject.Revit.Data;
using OpenProject.Revit.Extensions;
using OpenProject.Shared;
using OpenProject.Shared.ViewModels.Bcf;
using System;
using System.Collections.Generic;
using System.Linq;
using iabi.BCF.APIObjects.V21;
using OpenProject.Shared.Math3D;
using OpenProject.Shared.Math3D.Enumeration;
using Serilog;

namespace OpenProject.Revit.Entry
{
  /// <summary>
  /// Obfuscation Ignore for External Interface
  /// </summary>
  public class OpenViewpointEventHandler : IExternalEventHandler
  {
    private const decimal _viewpointAngleThresholdRad = 0.087266462599716m;

    /// <inheritdoc />
    public void Execute(UIApplication app)
    {
      ShowBcfViewpointInternal(app);
    }

    /// <inheritdoc />
    public string GetName() => nameof(OpenViewpointEventHandler);

    private BcfViewpointViewModel _bcfViewpoint;

    private static OpenViewpointEventHandler _instance;

    private static OpenViewpointEventHandler Instance
    {
      get
      {
        if (_instance != null) return _instance;

        _instance = new OpenViewpointEventHandler();
        ExternalEvent = ExternalEvent.Create(_instance);

        return _instance;
      }
    }

    private static ExternalEvent ExternalEvent { get; set; }

    /// <summary>
    /// Wraps the raising of the external event and thus the execution of the event callback,
    /// that show given bcf viewpoint.
    /// </summary>
    /// <param name="bcfViewpoint">The bcf viewpoint to be shown in current view.</param>
    public static void ShowBcfViewpoint(BcfViewpointViewModel bcfViewpoint)
    {
      Log.Information("Received 'Opening BCF Viewpoint event'. Attempting to open viewpoint ...");
      Instance._bcfViewpoint = bcfViewpoint;
      ExternalEvent.Raise();
    }

    private void ShowBcfViewpointInternal(UIApplication app)
    {
      try
      {
        Log.Information("Opening BCF Viewpoint ...");
        var hasCamera = _bcfViewpoint.GetCamera().Match(
          camera => ShowOpenProjectView(app, camera),
          () => false);
        if (!hasCamera)
        {
          Log.Error("BCF viewpoint has no camera information. Aborting ...");
          return;
        }

        UIDocument uiDoc = app.ActiveUIDocument;
        DeselectAndUnhideElements(uiDoc);
        ApplyElementStyles(_bcfViewpoint, uiDoc);
        ApplyClippingPlanes(_bcfViewpoint, uiDoc);

        Log.Information("Refreshing active UI view in Revit ...");
        uiDoc.RefreshActiveView();
        Log.Information("Finished updating all open views after loading BCF viewpoint.");
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to load BCF viewpoint");
        TaskDialog.Show("Error!", "exception: " + ex);
      }
    }

    private static bool ShowOpenProjectView(UIApplication app, Camera camera)
    {
      Log.Information("Opening related OpenProject view ...");

      Document doc = app.ActiveUIDocument.Document;
      View3D openProjectView = doc.GetOpenProjectView(camera.Type);

      XYZ cameraViewPoint = RevitUtils.GetRevitXYZ(camera.Viewpoint);
      XYZ cameraDirection = RevitUtils.GetRevitXYZ(camera.Direction);
      XYZ cameraUpVector = RevitUtils.GetRevitXYZ(camera.UpVector);

      ViewOrientation3D orient3D =
        RevitUtils.ConvertBasePoint(doc, cameraViewPoint, cameraDirection, cameraUpVector, true);

      Log.Information("Starting transaction to apply viewpoint orientation ...");
      using var trans = new Transaction(doc);
      if (trans.Start("Apply view camera") == TransactionStatus.Started)
      {
        if (camera.Type == CameraType.Perspective)
        {
          Parameter farClip = openProjectView.get_Parameter(BuiltInParameter.VIEWER_BOUND_ACTIVE_FAR);
          if (farClip.HasValue) farClip.Set(0);
        }

        openProjectView.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
        openProjectView.CropBoxActive = false;
        openProjectView.CropBoxVisible = false;
        openProjectView.SetOrientation(orient3D);
      }

      Log.Information("Committing transaction to apply viewpoint orientation ...");
      trans.Commit();

      Log.Information("Setting new view as active view of current UI document ...");
      app.ActiveUIDocument.ActiveView = openProjectView;

      if (camera.Type == CameraType.Orthogonal && camera is OrthogonalCamera orthoCam)
      {
        AppIdlingCallbackListener.SetPendingZoomChangedCallback(app, openProjectView.Id,
          orthoCam.ViewToWorldScale);
      }

      Log.Information("Finished opening OpenProject view of type {cameraType}", camera.Type.ToString());
      return true;
    }

    private static void DeselectAndUnhideElements(UIDocument uiDocument)
    {
      Log.Information("Applying object visibility ...");
      Document document = uiDocument.Document;

      using var transaction = new Transaction(uiDocument.Document);
      if (transaction.Start("Deselect selected and show hidden elements") == TransactionStatus.Started)
      {
        // This is to ensure no components are selected
        uiDocument.Selection.SetElementIds(new List<ElementId>());

        var hiddenRevitElements = new FilteredElementCollector(document)
          .WhereElementIsNotElementType()
          .WhereElementIsViewIndependent()
          .Where(e => e.IsHidden(document.ActiveView)) //might affect performance, but it's necessary
          .Select(e => e.Id)
          .ToList();

        if (hiddenRevitElements.Any())
        {
          Log.Information("Unhide {n} elements ...", hiddenRevitElements.Count);
          // Resetting hidden elements to show all elements in the model
          document.ActiveView.UnhideElements(hiddenRevitElements);
        }
      }

      Log.Information("Committing transaction to apply object visibility ...");
      transaction.Commit();
      Log.Information("Finished applying object visibility.");
    }

    private static void ApplyElementStyles(BcfViewpointViewModel bcfViewpoint, UIDocument uiDocument)
    {
      if (bcfViewpoint.Components?.Visibility == null)
        return;

      Log.Information("Applying object styles ...");
      Document document = uiDocument.Document;

      var visibleRevitElements = new FilteredElementCollector(document, document.ActiveView.Id)
        .WhereElementIsNotElementType()
        .WhereElementIsViewIndependent()
        .Where(e => e.CanBeHidden(document.ActiveView)) //might affect performance, but it's necessary
        .Select(e => e.Id)
        .ToList();

      // We're creating a dictionary of all the Revit internal Ids to be looked up by their IFC Guids
      // If this proves to be a performance issue, we should cache this dictionary in an instance variable
      var revitElementsByIfcGuid = new Dictionary<string, ElementId>();
      foreach (ElementId revitElement in visibleRevitElements)
      {
        var ifcGuid = IfcGuid.ToIfcGuid(ExportUtils.GetExportId(document, revitElement));
        if (!revitElementsByIfcGuid.ContainsKey(ifcGuid))
          revitElementsByIfcGuid.Add(ifcGuid, revitElement);
      }

      using var trans = new Transaction(uiDocument.Document);
      if (trans.Start("Apply BCF visibility and selection") == TransactionStatus.Started)
      {
        var exceptionElements = bcfViewpoint.Components.Visibility.Exceptions
          .Where(bcfComponentException => revitElementsByIfcGuid.ContainsKey(bcfComponentException.Ifc_guid))
          .Select(bcfComponentException => revitElementsByIfcGuid[bcfComponentException.Ifc_guid])
          .ToList();

        if (exceptionElements.Any())
          if (bcfViewpoint.Components.Visibility.Default_visibility)
            document.ActiveView.HideElementsTemporary(exceptionElements);
          else
            document.ActiveView.IsolateElementsTemporary(exceptionElements);

        if (bcfViewpoint.Components.Selection?.Any() ?? false)
        {
          var selectedElements = bcfViewpoint.Components.Selection
            .Where(selectedElement => revitElementsByIfcGuid.ContainsKey(selectedElement.Ifc_guid))
            .Select(selectedElement => revitElementsByIfcGuid[selectedElement.Ifc_guid])
            .ToList();

          if (selectedElements.Any())
          {
            Log.Information("Select {n} elements ...", selectedElements.Count);
            uiDocument.Selection.SetElementIds(selectedElements);
          }
        }
      }

      Log.Information("Committing transaction to apply object styles ...");
      trans.Commit();
      Log.Information("Finished applying object styles.");
    }

    private static void ApplyClippingPlanes(BcfViewpointViewModel bcfViewpoint, UIDocument uiDocument)
    {
      if (uiDocument.ActiveView is not View3D view3d)
        return;

      Log.Information("Applying clipping plane information ...");
      var clippingPlanes = bcfViewpoint.Viewpoint?.Clipping_planes ?? new List<Clipping_plane>();

      AxisAlignedBoundingBox boundingBox = clippingPlanes
        .Select(p => p.ToAxisAlignedBoundingBox(_viewpointAngleThresholdRad))
        .Aggregate(AxisAlignedBoundingBox.Infinite, (current, nextBox) => current.MergeReduce(nextBox));

      using var trans = new Transaction(uiDocument.Document);
      if (!boundingBox.Equals(AxisAlignedBoundingBox.Infinite))
      {
        Log.Information("Found axis aligned clipping planes. Start transaction to set resulting section box ...");
        if (trans.Start("Apply BCF section box") == TransactionStatus.Started)
        {
          view3d.SetSectionBox(ToRevitSectionBox(boundingBox));
          view3d.IsSectionBoxActive = true;
        }
      }
      else
      {
        Log.Information("Found no axis aligned clipping planes. Start transaction to disable section box ...");
        if (trans.Start("Disable section box") == TransactionStatus.Started)
          view3d.IsSectionBoxActive = false;
      }

      Log.Information("Committing transaction to apply clipping plane information ...");
      trans.Commit();
      Log.Information("Finished applying clipping plane information.");
    }

    private static BoundingBoxXYZ ToRevitSectionBox(AxisAlignedBoundingBox box)
    {
      var min = new XYZ(
        box.Min.X == decimal.MinValue ? double.MinValue : ((double)box.Min.X).ToInternalRevitUnit(),
        box.Min.Y == decimal.MinValue ? double.MinValue : ((double)box.Min.Y).ToInternalRevitUnit(),
        box.Min.Z == decimal.MinValue ? double.MinValue : ((double)box.Min.Z).ToInternalRevitUnit());
      var max = new XYZ(
        box.Max.X == decimal.MaxValue ? double.MaxValue : ((double)box.Max.X).ToInternalRevitUnit(),
        box.Max.Y == decimal.MaxValue ? double.MaxValue : ((double)box.Max.Y).ToInternalRevitUnit(),
        box.Max.Z == decimal.MaxValue ? double.MaxValue : ((double)box.Max.Z).ToInternalRevitUnit());

      return new BoundingBoxXYZ { Min = min, Max = max };
    }
  }
}
