#addin nuget:?package=Cake.Compression&version=0.2.4
#addin nuget:?package=SharpZipLib&version=1.1.0
#tool nuget:?package=GitVersion.CommandLine&version=4.0.0

//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var verbosity = Argument<Verbosity>("verbosity", Verbosity.Minimal);

//////////////////////////////////////////////////////////////////////
// PREPARATION READY
//////////////////////////////////////////////////////////////////

var baseName = "FinancialApp";
var buildDir = Directory("./build") + Directory(configuration);
var publishDir = Directory("./build/publish");
var assemblyInfoFile = Directory($"./src/{baseName}/Properties") + File("AssemblyInfo.cs");
var nodeEnv = configuration == "Release" ? "production" : "development";
var mainProjectPath = Directory("./src/App");

int p = (int) Environment.OSVersion.Platform;
bool isUnix = (p == 4) || (p == 6) || (p == 128);

var cmd = isUnix ? "bash" : "cmd";
var cmdArg = isUnix ? "-c" : "/C";

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .Does(() => {
    CleanDirectory(buildDir);
	CleanDirectory(publishDir);
	CleanDirectories("./src/App/bin");
	CleanDirectories("./src/App/obj");
});

Task("Rebuild")
	.IsDependentOn("Clean")
	.IsDependentOn("Build");
	
void RunCmd(string command) {
	var processSettings = new ProcessSettings()
		.WithArguments(args => args.Append(cmdArg).AppendQuoted(command))
	;
	

	var process = StartAndReturnProcess(cmd, processSettings);
	
	process.WaitForExit();
}
	
void CheckToolVersion(string name, string executable, string argument, Version wantedVersion) {
	try {
		Information($"Checking {name} version...");
		

		var processSettings = new ProcessSettings()
			.WithArguments(args => args.Append(cmdArg).AppendQuoted(executable + " " + argument))
			.SetRedirectStandardOutput(true)
		;
		

		var process = StartAndReturnProcess(cmd, processSettings);
		
		process.WaitForExit();
		
		string line = null;
		foreach (var output in process.GetStandardOutput()) {
			line = output;
			Debug(output);
		}
		
		if (String.IsNullOrEmpty(line)) {
			throw new CakeException("Didn't get any output from " + executable);
		}
	
		Version actualVersion = Version.Parse(line.Trim('v'));
		
		Information("Got version {0} - we want at least version {1}", actualVersion, wantedVersion);
		if (wantedVersion > actualVersion) {
			throw new CakeException($"{name} version {actualVersion} does not satisfy the requirement of {name}>={wantedVersion}");
		}
	} catch (Exception e) when (!(e is CakeException)) {
		throw new CakeException($"Unable to check {name} version. Please check whether {name} is available in the current %PATH%.", e);
	}
}

int StartProjectDirProcess(string processCommandLine) {
	return StartProcess(cmd, new ProcessSettings()
			.UseWorkingDirectory(mainProjectPath)
			.WithArguments(args => args.Append(cmdArg).AppendQuoted(processCommandLine)));
}
	
Task("Check-Node-Version")
	.Does(() => {
	CheckToolVersion("node.js", "node", "--version", new Version(10,16,0));
});

Task("Check-Yarn-Version")
	.Does(() => {
	CheckToolVersion("yarn package manager", "yarn", "--version", new Version(1,16,0) /*Minimum supported on appveyor*/);
});

Task("Restore-NuGet-Packages")
    .Does(() => {
    DotNetCoreRestore(new DotNetCoreRestoreSettings {
		IgnoreFailedSources = true
	});
});

Task("Generate-MigrationScript")
	.Does(() => {
		string workingDirectory = System.Environment.CurrentDirectory;
		
		// Work around the fact that Cake is not applying the working directory to the dotnet core executable
		try {
			System.Environment.CurrentDirectory = MakeAbsolute(mainProjectPath).ToString();
			
			DotNetCoreTool(
				"App.csproj", 
				"ef", 
				new ProcessArgumentBuilder()
					.Append("migrations")
					.Append("script")
					.Append("-o ../../build/publish/MigrationScript.sql"), 
				new DotNetCoreToolSettings  {
					WorkingDirectory = mainProjectPath, 
					DiagnosticOutput = true
				});
		} finally {
			System.Environment.CurrentDirectory = workingDirectory;
		}
	
	}
);
//comments
Task("Set-NodeEnvironment")
	.Does(() => {
		Information("Setting NODE_ENV to {0}", nodeEnv);
		
		System.Environment.SetEnvironmentVariable("NODE_ENV", nodeEnv);
	});

Task("Restore-Node-Packages")
	.IsDependentOn("Check-Node-Version")
	.IsDependentOn("Check-Yarn-Version")
	.Does(() => {
	
	int exitCode;
	
	Information("Trying to restore packages using yarn");
	
	exitCode = StartProjectDirProcess("yarn --production=false --frozen-lockfile --non-interactive");
		
	if (exitCode != 0) {
		throw new CakeException($"'yarn' returned exit code {exitCode} (0x{exitCode:x2})");
	}
});

Task("Build")
	.IsDependentOn("Set-NodeEnvironment")
    .IsDependentOn("Restore-NuGet-Packages")
    .IsDependentOn("Restore-Node-Packages")
    .Does(() => {
        DotNetCoreBuild($"./{baseName}.sln");
});

Task("Run")
    .IsDependentOn("Build")
    .Does(() => {
        DotNetCoreRun($"App.csproj", null, new DotNetCoreRunSettings { WorkingDirectory = "./src/App" });
});

void PublishSelfContained(string platform, string folder) {
	Information("Publishing self-contained for platform {0}", platform);

	var settings = new DotNetCorePublishSettings
			 {
				 Configuration = configuration,
				 OutputDirectory = publishDir + Directory(folder ?? platform),
				 Runtime = platform
			 };
	
    DotNetCorePublish($"./src/App/App.csproj", settings);
}

Task("Build-MailTemplates")
	.IsDependentOn("Restore-Node-Packages")
	.IsDependentOn("Set-NodeEnvironment")
	.Does(() => {
		var exitCode = StartProjectDirProcess("yarn run build-mailtemplates");
		
		if (exitCode != 0) {
			throw new CakeException($"'yarn run build-mailtemplates' returned exit code {exitCode} (0x{exitCode:x2})");
		}
	});

Task("Run-Webpack")
	.IsDependentOn("Restore-Node-Packages")
	.IsDependentOn("Set-NodeEnvironment")
	.Does(() => {
		var exitCode = StartProjectDirProcess("yarn run build");
		
		if (exitCode != 0) {
			throw new CakeException($"'yarn run build' returned exit code {exitCode} (0x{exitCode:x2})");
		}
	});
	
Task("Run-DotnetFormatToolInstall")
	.Does(() => {
	StartProcess("dotnet", "tool install --tool-path .dotnet/ dotnet-format");
});

IEnumerable<string> GetModifiedFilePaths() {
	IEnumerable<string> stdErr, stdOut;
	
	StartProcess(
		"git", 
		new ProcessSettings()
			.UseWorkingDirectory(mainProjectPath)
			.SetRedirectStandardError(true)
			.SetRedirectStandardOutput(true)
			.WithArguments(args => args.Append("status").Append("--porcelain=1")),
		out stdOut,
		out stdErr
	);
	
	foreach (string line in stdOut) {
		string fileName = line.Substring(3);
		
		if (System.IO.Path.GetExtension(fileName) != ".cs") {
			continue;
		}
		
		yield return fileName;
	}
}
	
Task("Run-Precommit-Tasks")
	.Does(() => {
	{
		int exitCode = StartProjectDirProcess("yarn pre-commit--pretty-quick");
		if (exitCode != 0) {
			throw new CakeException($"pretty-quick exited with code {exitCode}");
		}
	}
	
	{
		string filePaths = String.Join(",", GetModifiedFilePaths());
		
		if (String.IsNullOrEmpty(filePaths)) {
			Information("No changed files to reformat");
			return;
		}
		
		int exitCode = StartProcess("dotnet", new ProcessSettings()
			.UseWorkingDirectory(mainProjectPath)
			.WithArguments(args => args.Append("format").Append("--files").AppendQuoted(filePaths)));
			
		if (exitCode != 0) {
			throw new CakeException($"dotnet-format exited with code {exitCode}");
		}
	}
});

Task("Generate-Webpack-Statistics")
	.IsDependentOn("Restore-Node-Packages")
	.IsDependentOn("Set-NodeEnvironment")
	.Does(() => {
		Information("Setting WEBPACK_ENABLE_VISUALIZER to {0}", "true");
		
		System.Environment.SetEnvironmentVariable("WEBPACK_ENABLE_VISUALIZER", "true");
		
		var exitCode = StartProjectDirProcess(@"yarn run webpack-stats");
		
		if (exitCode != 0) {
			throw new CakeException($"'yarn run webpack-stats' returned exit code {exitCode} (0x{exitCode:x2})");
		}
	});

Task("Publish-Common")
	.Description("Internal task - do not use")
    .IsDependentOn("Rebuild")
    .IsDependentOn("Generate-MigrationScript")
	.IsDependentOn("Run-Webpack")
	.IsDependentOn("Build-MailTemplates");

var windowsAllPublishTask = Task("Publish-Windows");

void WindowsPublishTask(string taskId, string versionId, string description) {
	string internalTaskName = $"Publish-Windows-Core-{taskId}";
	string taskName = $"Publish-Windows-{taskId}";

	Task(internalTaskName)
		.Description("Internal task - do not use")
		.IsDependentOn("Publish-Common")
		.Does(() => PublishSelfContained(versionId, $"{versionId}/app"));

	var output = publishDir + File($"financial-app-{versionId}.zip");
	Task(taskName)
		.IsDependentOn(internalTaskName)
		.Description($"Publish for {description}, output to {output}")
		.Does(() => {
		   ZipCompress(publishDir + Directory($"{versionId}/"), output);
		});
	
	windowsAllPublishTask.IsDependentOn(taskName);
}

WindowsPublishTask("10-x64", "win10-x64", "Windows 10 / Windows Server 2016 64-bit");

var ubuntuAllPublishTask = Task("Publish-Ubuntu");

void UbuntuPublishTask(string taskId, string versionId, string description) {
	string internalTaskName = $"Publish-Ubuntu-Core-{taskId}";
	string taskName = $"Publish-Ubuntu-{taskId}";

	Task(internalTaskName)
		.Description("Internal task - do not use")
		.IsDependentOn("Publish-Common")
		.Does(() => PublishSelfContained(versionId, $"{versionId}/app"));

	var output = publishDir + File($"financial-app-{versionId}.tar.gz");
	Task(taskName)
		.IsDependentOn(internalTaskName)
		.Description($"Publish for {description}, output to {output}")
		.Does(() => {
		   CopyFile(File("./distscripts/ubuntu/launch"), publishDir + File($"{versionId}/launch"));
		   CopyFile(File("./distscripts/ubuntu/launch.conf"), publishDir + File($"{versionId}/launch.conf.example"));
		   GZipCompress(publishDir + Directory($"{versionId}/"), output);
		});
	
	ubuntuAllPublishTask.IsDependentOn(taskName);
}

UbuntuPublishTask("16.10-x64", "ubuntu.16.10-x64", "Ubuntu 16.10/17.04 64-bit");
UbuntuPublishTask("18.04-x64", "ubuntu.18.04-x64", "Ubuntu 18.04 64-bit");
//UbuntuPublishTask("16.10-x64-ngen", "ubuntu.16.10-x64-corert", "Ubuntu 16.10/17.04 64-bit - experimental ahead-of-time compiled version");

Task("Publish")
    .IsDependentOn("Publish-Windows")
    .IsDependentOn("Publish-Ubuntu");
	
Task("Set-HeadlessEnvironment")
	.Does(() => {
		Information("Setting MOZ_HEADLESS to 1");
		
		System.Environment.SetEnvironmentVariable("MOZ_HEADLESS", "1");
	});

Task("Test-JS")
    .IsDependentOn("Run-Webpack")
	.IsDependentOn("Set-HeadlessEnvironment")
    .Description("Test javascript front-end code")
    .Does(() => {
		var exitCode = StartProjectDirProcess("yarn run test");
		
		if (exitCode != 0) {
			throw new CakeException($"'yarn run test' returned exit code {exitCode} (0x{exitCode:x2})");
		}
	});

Task("Test-CS")
	.IsDependentOn("Restore-NuGet-Packages")
    .Description("Test backend-end compiled code")
	.Does(() => DotNetCoreTest($"./src/App.Tests/App.Tests.csproj"));

Task("Test")
    .IsDependentOn("Test-CS")
	.IsDependentOn("Test-JS")
    .Description("Run all tests");

//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("None");

Task("Default")
    .IsDependentOn("Build");

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

