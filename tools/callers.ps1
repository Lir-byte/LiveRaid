param([string]$Dll, [string]$Callee)
Add-Type -Path "C:\Games\SPT\SPT_Runtime\Mono.Cecil.dll" -ErrorAction SilentlyContinue
$rp = New-Object Mono.Cecil.ReaderParameters
$res = New-Object Mono.Cecil.DefaultAssemblyResolver
$res.AddSearchDirectory("C:\Games\SPT\EscapeFromTarkov_Data\Managed")
$res.AddSearchDirectory("C:\Games\SPT\BepInEx\core")
$rp.AssemblyResolver = $res
$a = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Dll, $rp)
function AllTypes($types) { foreach ($t in $types) { $t; if ($t.HasNestedTypes) { AllTypes $t.NestedTypes } } }
foreach ($t in (AllTypes $a.MainModule.Types)) {
  foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    foreach ($ins in $m.Body.Instructions) {
      if (($ins.OpCode.Name -eq "call" -or $ins.OpCode.Name -eq "callvirt" -or $ins.OpCode.Name -eq "ldftn") -and $ins.Operand -ne $null -and $ins.Operand.ToString() -match $Callee) {
        $t.FullName + "::" + $m.Name + "  ->  " + $ins.Operand.ToString()
      }
    }
  }
}
