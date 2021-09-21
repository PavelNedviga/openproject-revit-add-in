﻿using Config.Net;
using System;
using System.IO;
using System.Reflection;
using OpenProject.Shared;

namespace OpenProject.Revit.Entry
{
  public static class ConfigurationLoader
  {
    static ConfigurationLoader()
    {
      var configurationFilePath = GetConfigurationFilePath();
      Settings = new ConfigurationBuilder<IOpenProjectRevitSettings>()
        .UseJsonFile(configurationFilePath)
        .Build();
    }

    public static IOpenProjectRevitSettings Settings { get; }

    public static string GetBcfierWinExecutablePath()
    {
      var bcfierWinExecutablePath = Settings.OpenProjectWindowsExecutablePath;
      if (!Path.IsPathRooted(bcfierWinExecutablePath))
      {
        var currentFolder = GetCurrentDllDirectory();
        bcfierWinExecutablePath = Path.Combine(currentFolder, bcfierWinExecutablePath);
      }

      if (!File.Exists(bcfierWinExecutablePath))
      {
        throw new Exception(
          $"The OpenProject.Browser.exe path in the configuration is given as: \"{bcfierWinExecutablePath}\", but the file could not be found.");
      }

      return bcfierWinExecutablePath;
    }

    private static string GetConfigurationFilePath()
    {
      var configPath = Path.Combine(
        ConfigurationConstant.OpenProjectApplicationData,
        "OpenProject.Revit.Configuration.json");

      if (File.Exists(configPath)) return configPath;

      // If the file doesn't yet exist, the default one is created
      using Stream configStream =
        typeof(ConfigurationLoader).Assembly.GetManifestResourceStream(
          "OpenProject.Revit.OpenProject.Revit.Configuration.json");
      var configDirName = Path.GetDirectoryName(configPath);
      if (!Directory.Exists(configDirName)) Directory.CreateDirectory(configDirName);

      using FileStream fs = File.Create(configPath);
      configStream.CopyTo(fs);

      return configPath;
    }

    private static string GetCurrentDllDirectory()
    {
      var currentAssemblyPathUri = Assembly.GetExecutingAssembly().CodeBase;
      var currentAssemblyPath = Uri.UnescapeDataString(new Uri(currentAssemblyPathUri).AbsolutePath)
        // '/' comes from the uri, we need it to be '\' for the path
        .Replace("/", "\\");
      var currentFolder = Path.GetDirectoryName(currentAssemblyPath);
      return currentFolder;
    }
  }
}
