$fw  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$csc = "$fw\csc.exe"
& $csc /nologo /target:winexe /optimize+ /codepage:65001 /out:"$PSScriptRoot\ClipVault.exe" `
  /win32icon:"$PSScriptRoot\icon.ico" `
  /lib:"$fw\WPF" `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll `
  /r:UIAutomationClient.dll /r:UIAutomationTypes.dll /r:WindowsBase.dll `
  "$PSScriptRoot\ClipVault.cs"
