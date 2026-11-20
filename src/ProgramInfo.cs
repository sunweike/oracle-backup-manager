using System.Reflection;

[assembly: AssemblyProduct("Oracle备份管理系统")]
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]

#if CLIENT_BUILD
[assembly: AssemblyTitle("Oracle备份端")]
[assembly: AssemblyDescription("Oracle备份端")]
#else
[assembly: AssemblyTitle("Oracle接受端")]
[assembly: AssemblyDescription("Oracle接受端")]
#endif
