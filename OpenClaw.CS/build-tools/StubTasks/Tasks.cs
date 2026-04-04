using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.AppxPackage
{
    public class RemovePayloadDuplicates : Task
    {
        public ITaskItem[] Inputs { get; set; }
        public string ProjectName { get; set; }
        public string Platform { get; set; }
        public string VsTelemetrySession { get; set; }
        [Output] public ITaskItem[] Filtered { get; set; }
        public override bool Execute() { Filtered = Inputs ?? new ITaskItem[0]; return true; }
    }

    public class ExpandPayloadDirectories : Task
    {
        public ITaskItem[] Inputs { get; set; }
        public string VsTelemetrySession { get; set; }
        [Output] public ITaskItem[] Expanded { get; set; }
        public override bool Execute() { Expanded = Inputs ?? new ITaskItem[0]; return true; }
    }

    public class GetDefaultResourceLanguage : Task
    {
        public string DefaultLanguage { get; set; }
        public ITaskItem[] SourceAppxManifest { get; set; }
        public string VsTelemetrySession { get; set; }
        [Output] public string DefaultResourceLanguage { get; set; }
        public override bool Execute() { DefaultResourceLanguage = DefaultLanguage ?? "en-US"; return true; }
    }

    public class GetPackageArchitecture : Task
    {
        public string Platform { get; set; }
        public ITaskItem[] ProjectArchitecture { get; set; }
        public ITaskItem[] RecursiveProjectArchitecture { get; set; }
        public string VsTelemetrySession { get; set; }
        [Output] public string PackageArchitecture { get; set; }
        public override bool Execute() { PackageArchitecture = Platform ?? "x64"; return true; }
    }

    public class GetSdkFileFullPath : Task
    {
        public string SdkToolsPath { get; set; }
        public string SdkBinPath { get; set; }
        public string FileName { get; set; }
        public string SDKIdentifier { get; set; }
        public string SDKVersion { get; set; }
        public string TargetPlatformIdentifier { get; set; }
        public string TargetPlatformMinVersion { get; set; }
        public string TargetPlatformVersion { get; set; }
        public string VsTelemetrySession { get; set; }
        [Output] public string SdkFileFullPath { get; set; }
        public override bool Execute() { SdkFileFullPath = ""; return true; }
    }

    public class GetSdkPropertyValue : Task
    {
        public string TargetPlatformSdkRootOverride { get; set; }
        public string SDKIdentifier { get; set; }
        public string SDKVersion { get; set; }
        public string TargetPlatformIdentifier { get; set; }
        public string TargetPlatformMinVersion { get; set; }
        public string TargetPlatformVersion { get; set; }
        public string PropertyName { get; set; }
        public string VsTelemetrySession { get; set; }
        [Output] public string SdkPropertyValue { get; set; }
        public override bool Execute() { SdkPropertyValue = ""; return true; }
    }

    public class RemoveRedundantXamlFilesFromSdkPayload : Task
    {
        public ITaskItem[] Inputs { get; set; }
        public string VsTelemetrySession { get; set; }
        [Output] public ITaskItem[] Filtered { get; set; }
        public override bool Execute() { Filtered = Inputs ?? new ITaskItem[0]; return true; }
    }

    public class ValidateConfiguration : Task
    {
        public string TargetPlatformMinVersion { get; set; }
        public string TargetPlatformVersion { get; set; }
        public string ProjectLanguage { get; set; }
        public string VsTelemetrySession { get; set; }
        public string TargetPlatformIdentifier { get; set; }
        public string Platform { get; set; }
        public override bool Execute() { return true; }
    }
}
