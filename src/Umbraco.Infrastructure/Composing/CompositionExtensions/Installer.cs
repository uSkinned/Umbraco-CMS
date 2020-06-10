﻿using Umbraco.Core;
using Umbraco.Core.Composing;
using Umbraco.Web.Install;
using Umbraco.Web.Install.InstallSteps;
using Umbraco.Web.Install.Models;

namespace Umbraco.Web.Composing.CompositionExtensions
{
    public static class Installer
    {
        public static Composition ComposeInstaller(this Composition composition)
        {
            // register the installer steps

            composition.Register<NewInstallStep>(Lifetime.Scope);
            composition.Register<UpgradeStep>(Lifetime.Scope);
            composition.Register<FilePermissionsStep>(Lifetime.Scope);
            composition.Register<DatabaseConfigureStep>(Lifetime.Scope);
            composition.Register<DatabaseInstallStep>(Lifetime.Scope);
            composition.Register<DatabaseUpgradeStep>(Lifetime.Scope);

            // TODO: Add these back once we have a compatible Starter kit
            // composition.Register<StarterKitDownloadStep>(Lifetime.Scope);
            // composition.Register<StarterKitInstallStep>(Lifetime.Scope);
            // composition.Register<StarterKitCleanupStep>(Lifetime.Scope);

            composition.Register<CompleteInstallStep>(Lifetime.Scope);

            composition.Register<InstallStepCollection>();
            composition.RegisterUnique<InstallHelper>();

            return composition;
        }
    }
}
