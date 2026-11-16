using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LordsBotGate;

internal static class DynamicDetour
{
	private static readonly Lazy<(ModuleBuilder Module, Type IgnoresAttrType)> s_dyn = new Lazy<(ModuleBuilder, Type)>(InitDynamicModule);

	private static readonly object s_lock = new object();

	private static int s_counter;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool VirtualProtect(nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentProcess();

	[DllImport("kernel32.dll")]
	private static extern bool FlushInstructionCache(nint hProcess, nint lpBaseAddress, nuint dwSize);

	private static (ModuleBuilder Module, Type IgnoresAttrType) InitDynamicModule()
	{
		AssemblyBuilder assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("LordsBotGate.DynamicDetours"), AssemblyBuilderAccess.Run);
		ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule("Detours");
		TypeBuilder typeBuilder = moduleBuilder.DefineType("System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute", TypeAttributes.Public | TypeAttributes.Sealed, typeof(Attribute));
		FieldBuilder field = typeBuilder.DefineField("AssemblyName", typeof(string), FieldAttributes.Private | FieldAttributes.InitOnly);
		ILGenerator iLGenerator = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new Type[1] { typeof(string) }).GetILGenerator();
		ConstructorInfo constructor = typeof(Attribute).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
		iLGenerator.Emit(OpCodes.Ldarg_0);
		iLGenerator.Emit(OpCodes.Call, constructor);
		iLGenerator.Emit(OpCodes.Ldarg_0);
		iLGenerator.Emit(OpCodes.Ldarg_1);
		iLGenerator.Emit(OpCodes.Stfld, field);
		iLGenerator.Emit(OpCodes.Ret);
		PropertyBuilder propertyBuilder = typeBuilder.DefineProperty("AssemblyName", PropertyAttributes.None, typeof(string), null);
		MethodBuilder methodBuilder = typeBuilder.DefineMethod("get_AssemblyName", MethodAttributes.Public | MethodAttributes.SpecialName, typeof(string), Type.EmptyTypes);
		ILGenerator iLGenerator2 = methodBuilder.GetILGenerator();
		iLGenerator2.Emit(OpCodes.Ldarg_0);
		iLGenerator2.Emit(OpCodes.Ldfld, field);
		iLGenerator2.Emit(OpCodes.Ret);
		propertyBuilder.SetGetMethod(methodBuilder);
		ConstructorInfo constructor2 = typeof(AttributeUsageAttribute).GetConstructor(new Type[1] { typeof(AttributeTargets) });
		PropertyInfo property = typeof(AttributeUsageAttribute).GetProperty("AllowMultiple");
		typeBuilder.SetCustomAttribute(new CustomAttributeBuilder(constructor2, new object[1] { AttributeTargets.Assembly }, new PropertyInfo[1] { property }, new object[1] { true }));
		Type type = typeBuilder.CreateType();
		ConstructorInfo constructor3 = type.GetConstructor(new Type[1] { typeof(string) });
		assemblyBuilder.SetCustomAttribute(new CustomAttributeBuilder(constructor3, new object[1] { "LordsMobileBot" }));
		assemblyBuilder.SetCustomAttribute(new CustomAttributeBuilder(constructor3, new object[1] { "Gate" }));
		return (Module: moduleBuilder, IgnoresAttrType: type);
	}

	public static void Redirect(MethodInfo target, MethodInfo handler, Action<string>? trace = null)
	{
		if (target == null)
		{
			throw new ArgumentNullException("target");
		}
		if (handler == null)
		{
			throw new ArgumentNullException("handler");
		}
		MethodInfo replacement = BuildMatchingMethod(target, handler);
		PatchMethodJump(target, replacement, trace);
	}

	private static MethodInfo BuildMatchingMethod(MethodInfo target, MethodInfo handler)
	{
		ParameterInfo[] parameters = target.GetParameters();
		List<Type> list = new List<Type>(parameters.Length + 1);
		if (!target.IsStatic)
		{
			Type type = target.DeclaringType ?? typeof(object);
			list.Add(type.IsValueType ? type.MakeByRefType() : type);
		}
		ParameterInfo[] array = parameters;
		foreach (ParameterInfo parameterInfo in array)
		{
			list.Add(parameterInfo.ParameterType);
		}
		ParameterInfo[] parameters2 = handler.GetParameters();
		if (parameters2.Length == list.Count && handler.ReturnType == target.ReturnType)
		{
			bool flag = true;
			for (int j = 0; j < parameters2.Length; j++)
			{
				if (!parameters2[j].ParameterType.IsAssignableFrom(list[j]))
				{
					flag = false;
					break;
				}
			}
			if (flag)
			{
				return handler;
			}
		}
		lock (s_lock)
		{
			ModuleBuilder item = s_dyn.Value.Module;
			int value = ++s_counter;
			TypeBuilder typeBuilder = item.DefineType($"Thunk_{value}", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
			ILGenerator iLGenerator = typeBuilder.DefineMethod("Invoke", MethodAttributes.Public | MethodAttributes.Static, target.ReturnType, list.ToArray()).GetILGenerator();
			bool[] array2 = new bool[list.Count];
			for (int k = 0; k < parameters2.Length; k++)
			{
				Type parameterType = parameters2[k].ParameterType;
				int num = -1;
				if (k == 0 && !target.IsStatic && parameterType.IsAssignableFrom(list[0]))
				{
					num = 0;
				}
				else
				{
					int num2 = ((!target.IsStatic) ? 1 : 0);
					for (int l = num2; l < list.Count; l++)
					{
						if (!array2[l] && list[l] == parameterType)
						{
							num = l;
							break;
						}
					}
					if (num < 0)
					{
						for (int m = num2; m < list.Count; m++)
						{
							if (!array2[m] && !list[m].IsValueType && !list[m].IsByRef && parameterType.IsAssignableFrom(list[m]))
							{
								num = m;
								break;
							}
						}
					}
				}
				if (num >= 0)
				{
					array2[num] = true;
					EmitLdArg(iLGenerator, num);
					continue;
				}
				if (!parameterType.IsValueType)
				{
					iLGenerator.Emit(OpCodes.Ldnull);
					continue;
				}
				LocalBuilder local = iLGenerator.DeclareLocal(parameterType);
				iLGenerator.Emit(OpCodes.Ldloca_S, local);
				iLGenerator.Emit(OpCodes.Initobj, parameterType);
				iLGenerator.Emit(OpCodes.Ldloc, local);
			}
			iLGenerator.Emit(OpCodes.Call, handler);
			if (target.ReturnType == typeof(void))
			{
				if (handler.ReturnType != typeof(void))
				{
					iLGenerator.Emit(OpCodes.Pop);
				}
			}
			else if (handler.ReturnType != target.ReturnType)
			{
				if (handler.ReturnType == typeof(void))
				{
					if (!target.ReturnType.IsValueType)
					{
						iLGenerator.Emit(OpCodes.Ldnull);
					}
					else
					{
						LocalBuilder local2 = iLGenerator.DeclareLocal(target.ReturnType);
						iLGenerator.Emit(OpCodes.Ldloca_S, local2);
						iLGenerator.Emit(OpCodes.Initobj, target.ReturnType);
						iLGenerator.Emit(OpCodes.Ldloc, local2);
					}
				}
				else if (!target.ReturnType.IsAssignableFrom(handler.ReturnType) && !target.ReturnType.IsValueType)
				{
					iLGenerator.Emit(OpCodes.Castclass, target.ReturnType);
				}
			}
			iLGenerator.Emit(OpCodes.Ret);
			return typeBuilder.CreateType().GetMethod("Invoke", BindingFlags.Static | BindingFlags.Public);
		}
	}

	private static void EmitLdArg(ILGenerator il, int index)
	{
		switch (index)
		{
		case 0:
			il.Emit(OpCodes.Ldarg_0);
			return;
		case 1:
			il.Emit(OpCodes.Ldarg_1);
			return;
		case 2:
			il.Emit(OpCodes.Ldarg_2);
			return;
		case 3:
			il.Emit(OpCodes.Ldarg_3);
			return;
		}
		if (index <= 255)
		{
			il.Emit(OpCodes.Ldarg_S, (byte)index);
		}
		else
		{
			il.Emit(OpCodes.Ldarg, index);
		}
	}

	private unsafe static void WriteMovRaxJmp(nint address, nint destination)
	{
		byte* ptr = (byte*)((IntPtr)address).ToPointer();
		if (!VirtualProtect((nint)ptr, 16u, 64u, out var lpflOldProtect))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}
		try
		{
			*ptr = 72;
			ptr[1] = 184;
			*(long*)(ptr + 2) = ((IntPtr)destination).ToInt64();
			ptr[10] = byte.MaxValue;
			ptr[11] = 224;
		}
		finally
		{
			VirtualProtect((nint)ptr, 16u, lpflOldProtect, out var _);
		}
		FlushInstructionCache(GetCurrentProcess(), (nint)ptr, 16u);
	}

	private unsafe static void PatchMethodJump(MethodBase target, MethodInfo replacement, Action<string>? trace)
	{
		RuntimeHelpers.PrepareMethod(target.MethodHandle);
		RuntimeHelpers.PrepareMethod(replacement.MethodHandle);
		nint functionPointer = target.MethodHandle.GetFunctionPointer();
		nint functionPointer2 = replacement.MethodHandle.GetFunctionPointer();
		if (functionPointer == IntPtr.Zero || functionPointer2 == IntPtr.Zero)
		{
			throw new InvalidOperationException("Function pointer null for " + target.Name);
		}
		byte* ptr = (byte*)((IntPtr)functionPointer).ToPointer();
		if (*ptr == byte.MaxValue && ptr[1] == 37)
		{
			int num = *(int*)(ptr + 2);
			nint* ptr2 = (nint*)(ptr + 6 + num);
			if (*ptr2 == functionPointer2)
			{
				return;
			}
			nint num2 = *ptr2;
			if (num2 != IntPtr.Zero && num2 != functionPointer)
			{
				try
				{
					WriteMovRaxJmp(num2, functionPointer2);
				}
				catch
				{
				}
			}
			if (VirtualProtect((nint)ptr2, (nuint)IntPtr.Size, 64u, out var lpflOldProtect))
			{
				*ptr2 = functionPointer2;
				VirtualProtect((nint)ptr2, (nuint)IntPtr.Size, lpflOldProtect, out var _);
				FlushInstructionCache(GetCurrentProcess(), (nint)ptr2, (nuint)IntPtr.Size);
			}
			return;
		}
		if (*ptr == 233)
		{
			int num3 = *(int*)(ptr + 1);
			nint num4 = functionPointer + 5 + num3;
			if (num4 != functionPointer && num4 != functionPointer2)
			{
				try
				{
					WriteMovRaxJmp(num4, functionPointer2);
				}
				catch
				{
				}
			}
		}
		WriteMovRaxJmp(functionPointer, functionPointer2);
	}
}
