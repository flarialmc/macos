using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: AssemblyCompany("Flarial")]
[assembly: AssemblyProduct("Runtime")]
[assembly: AssemblyTitle("Flarial Runtime")]
[assembly: AssemblyCopyright("Copyright © Flarial 2025 - 2026")]

// the Linux backend reaches the account token and the loader payload (self-tests); GenerateAssemblyInfo is off, so an InternalsVisibleTo item would be ignored
[assembly: InternalsVisibleTo("Flarial.Runtime.Linux")]
[assembly: InternalsVisibleTo("Flarial.Runtime.Mac")]