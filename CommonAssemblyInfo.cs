// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;

#if NET6_0_OR_GREATER
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
#endif

// What the executable says about itself in its file properties, in the status bar and on the splash
// screen. These were the inherited project's own values, including its placeholder 33.33.33 version,
// which the status bar was faithfully reporting as Gitxen's.
//
// The version is kept in step with setup/gitxen/version.txt by hand: that file is what the installer
// and the package manifests are built from, and a release where the two disagree is worse than one
// that has to be edited in two places.
[assembly: AssemblyTitle("Gitxen")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Kakoso")]
[assembly: AssemblyProduct("Gitxen")]
[assembly: AssemblyCopyright("Copyright © 2026 Kakoso")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: AssemblyVersion("0.10.1")]
[assembly: AssemblyFileVersion("0.10.1")]
[assembly: AssemblyInformationalVersion("0.10.1")]

// CLS compliance is not claimed: nothing here is meant to be consumed from another language.
[assembly: CLSCompliant(isCompliant: false)]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]
