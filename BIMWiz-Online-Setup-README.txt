BIMWiz Online Setup

FOR USERS
Download BIMWiz-Online-Setup.exe from the website and run it on Windows 10 or 11.
Choose "Download & open installer". Setup downloads the latest release ZIP from
the bimwizard/bimwiz GitHub repository, checks its size and SHA-256 checksum,
extracts the package, and opens the existing installer.
Choose the Revit version and Install, Repair, or Uninstall in that installer.
The current package includes Revit 2024 and Revit 2026.
No manual ZIP extraction or .NET SDK installation is required.
The online setup uses .NET Framework, which is included with Windows 10/11.

Internet access to raw.githubusercontent.com is required. The offline ZIP is
still available on the website. The EXE is not code-signed; Windows may show
an unknown-publisher or SmartScreen prompt. This does not bypass those prompts.

Release files remain at %LOCALAPPDATA%\BIMWiz\OnlineSetup so the downloaded
installer can continue using its payload and logos. Revit installation and
logging behavior are handled by the original installer.

FOR THE REPOSITORY MAINTAINER
Files:
  BIMWiz-Online-Setup.exe       Small online launcher
  bimwiz-release.json          Current release version, URL, size, SHA-256
  BIMWiz-Installer-Package.zip  Existing complete offline installer package
  BIMWiz.OnlineSetup.cs        Online launcher source
  Build-Online-Setup.ps1        Rebuild launcher and regenerate release metadata
  BIMWiz-logo.png              Embedded launcher logo

To publish an update:
1. Replace BIMWiz-Installer-Package.zip with the tested new installer package.
   Set the same release version in each payload/<year>/version.txt file.
2. Run Build-Online-Setup.ps1 in the repository folder. It uses the Windows
   .NET Framework C# compiler and needs no additional SDK or downloaded tools.
3. Commit the updated ZIP and bimwiz-release.json together. Also commit the
   EXE if you changed the launcher source or branding.

Existing online launchers read bimwiz-release.json each time they run, so
package updates do not require users to download a new launcher.
The checksum detects corrupt or mismatched downloads. The GitHub repository
remains the trusted publication source; checksums are not a code signature.
