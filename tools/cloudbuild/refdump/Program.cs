using Mono.Cecil;
using System;
using System.Linq;
using System.Collections.Generic;

// Dumps every external type/member reference of an assembly as "[scope] member" lines, sorted.
class P
{
    static void Main(string[] a)
    {
        var m = ModuleDefinition.ReadModule(a[0]);
        var set = new SortedSet<string>();
        foreach (var t in m.GetTypeReferences()) set.Add("T [" + t.Scope.Name + "] " + t.FullName);
        foreach (var r in m.GetMemberReferences())
        {
            var dt = r.DeclaringType;
            var scope = dt?.GetElementType()?.Scope?.Name ?? "?";
            if (scope == m.Name) continue;
            set.Add("M [" + scope + "] " + r.FullName);
        }
        foreach (var s in set) Console.WriteLine(s);
    }
}
