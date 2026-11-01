param(
  [string]$Dll,
  [string]$TypePattern = "",
  [switch]$Methods,
  [switch]$Fields,
  [switch]$Attrs,
  [string]$MethodPattern = "",
  [switch]$Body
)
Add-Type -Path "C:\Games\SPT\SPT_Runtime\Mono.Cecil.dll" -ErrorAction SilentlyContinue
$rp = New-Object Mono.Cecil.ReaderParameters
$res = New-Object Mono.Cecil.DefaultAssemblyResolver
$res.AddSearchDirectory("C:\Games\SPT\EscapeFromTarkov_Data\Managed")
$res.AddSearchDirectory("C:\Games\SPT\BepInEx\core")
$res.AddSearchDirectory("C:\Games\SPT\BepInEx\plugins")
$res.AddSearchDirectory("C:\Games\SPT\BepInEx\plugins\spt")
$rp.AssemblyResolver = $res
$a = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Dll, $rp)
if ($Attrs) { $a.CustomAttributes | ForEach-Object { $_.AttributeType.FullName + " " + (($_.ConstructorArguments | ForEach-Object { $_.Value }) -join ", ") }; $a.MainModule.AssemblyReferences | ForEach-Object { "ref " + $_.FullName } }
function AllTypes($types) { foreach ($t in $types) { $t; if ($t.HasNestedTypes) { AllTypes $t.NestedTypes } } }
$types = AllTypes $a.MainModule.Types | Where-Object { $TypePattern -eq "" -or $_.FullName -match $TypePattern }
foreach ($t in $types) {
  $base = if ($t.BaseType) { " : " + $t.BaseType.FullName } else { "" }
  "TYPE " + $t.FullName + $base
  if ($Fields) { foreach ($f in $t.Fields) { "   F " + $(if($f.IsStatic){"static "}else{""}) + $f.FieldType.FullName + " " + $f.Name }; foreach ($p in $t.Properties) { "   P " + $p.PropertyType.FullName + " " + $p.Name } }
  if ($Methods) {
    foreach ($m in $t.Methods) {
      if ($MethodPattern -ne "" -and $m.Name -notmatch $MethodPattern) { continue }
      $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.FullName + " " + $_.Name }) -join ", "
      $vis = if ($m.IsPublic) {"pub"} elseif ($m.IsPrivate) {"priv"} else {"int"}
      "   M $vis " + $(if($m.IsStatic){"static "}else{""}) + $(if($m.IsVirtual){"virt "}else{""}) + $m.ReturnType.FullName + " " + $m.Name + "(" + $ps + ")"
      if ($Body -and $m.HasBody) { foreach ($ins in $m.Body.Instructions) { "        " + $ins.OpCode.Name + " " + $(if ($ins.Operand -ne $null) { $ins.Operand.ToString() } else { "" }) } }
    }
  }
}
