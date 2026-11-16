#r "E:/mylb/LordsBotGate/LordsMobileBot.dll"
using System;
using System.Reflection;
using System.Linq;

var asm = Assembly.LoadFrom(@"E:\mylb\LordsBotGate\LordsMobileBot.dll");
var bType = asm.GetTypes().FirstOrDefault(t => t.Name == "bulkCreateAccountForm");
Console.WriteLine("bType: " + bType?.FullName);
if (bType != null)
{
    var m = bType.GetMethod("getProxy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    Console.WriteLine("getProxy: " + m);
    if (m != null)
    {
        Console.WriteLine("ReturnType: " + m.ReturnType.FullName);
        foreach (var p in m.GetParameters())
        {
            Console.WriteLine($"Param: {p.Name} : {p.ParameterType.FullName}");
        }
    }
}
