using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace LordsBotGate;

public static class BotResolver
{
	public static BotSchema Resolve(Assembly asm)
	{
		if (asm == null)
		{
			throw new ArgumentNullException("asm");
		}
		Type[] source;
		try
		{
			source = asm.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			source = ex.Types.Where((Type t) => t != null).ToArray();
		}
		Type type = source.FirstOrDefault((Type t) => t.Name == "AccountWindow") ?? throw new InvalidOperationException("AccountWindow type not found in LordsMobileBot.dll");
		FieldInfo fieldInfo = type.GetField("runningAccounts", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new InvalidOperationException("AccountWindow.runningAccounts not found");
		FieldInfo field = type.GetField("hasServerConn", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		FieldInfo fieldInfo2 = type.GetField("gameLangID", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new InvalidOperationException("AccountWindow.gameLangID not found");
		Type syncCacheType = fieldInfo.FieldType;
		ConstructorInfo syncCacheCtorLong = syncCacheType.GetConstructor(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[1] { typeof(long) }, null) ?? throw new InvalidOperationException(syncCacheType.Name + "(long) ctor not found");
		Type fieldType = fieldInfo2.FieldType;
		object gameLangEngValue = Enum.Parse(fieldType, "GL_Eng");
		Type cont = source.FirstOrDefault((Type t) => !t.IsNested && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => !f.IsStatic && f.FieldType.Name == "GBSettings") && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => !f.IsStatic && f.FieldType.Name == "MGSettings") && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => !f.IsStatic && f.FieldType.Name == "accountRecord") && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => !f.IsStatic && f.FieldType == typeof(string[]))) ?? throw new InvalidOperationException("Account Container type (GBSettings/MGSettings/string[]) not found");
		FieldInfo containerGBSettingsField = cont.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).First((FieldInfo f) => !f.IsStatic && f.FieldType.Name == "GBSettings");
		FieldInfo containerMGSettingsField = cont.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).First((FieldInfo f) => !f.IsStatic && f.FieldType.Name == "MGSettings");
		FieldInfo containerCoordArrayField = cont.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).First((FieldInfo f) => !f.IsStatic && f.FieldType == typeof(string[]));
		FieldInfo containerAccRecordField = cont.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).First((FieldInfo f) => !f.IsStatic && f.FieldType.Name == "accountRecord");
		Type ciw = source.FirstOrDefault((Type t) => !t.IsNested && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => !f.IsStatic && f.FieldType.Name == "CallInvoker") && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Count((FieldInfo f) => f.IsStatic && f.FieldType.Name.StartsWith("Method`", StringComparison.Ordinal)) >= 8) ?? throw new InvalidOperationException("CallInvokerWrapper type not found");
		FieldInfo callInvokerField = ciw.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).First((FieldInfo f) => !f.IsStatic && f.FieldType.Name == "CallInvoker");
		Type sc = source.FirstOrDefault((Type t) => !t.IsNested && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => !f.IsStatic && f.FieldType.Name == "GrpcChannel") && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => !f.IsStatic && f.FieldType == ciw) && t.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Length >= 10) ?? throw new InvalidOperationException("StaticClient type not found");
		ConstructorInfo constructorInfo = sc.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((ConstructorInfo c) => c.GetParameters().Length == 1 && !c.GetParameters()[0].ParameterType.IsPrimitive && c.GetParameters()[0].ParameterType != typeof(string)) ?? throw new InvalidOperationException("StaticClient(EndpointListHolder) ctor not found");
		Type parameterType = constructorInfo.GetParameters()[0].ParameterType;
		FieldInfo endpointListField = parameterType.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => !f.IsStatic && f.FieldType == typeof(IReadOnlyList<string>)) ?? parameterType.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => !f.IsStatic && typeof(IEnumerable<string>).IsAssignableFrom(f.FieldType)) ?? throw new InvalidOperationException("Endpoint list field not found on " + parameterType.Name);
		FieldInfo[] staticClientWrapperFields = (from f in sc.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
			where !f.IsStatic && f.FieldType == ciw
			select f).ToArray();
		FieldInfo[] staticClientBoolFields = (from f in sc.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
			where !f.IsStatic && f.FieldType == typeof(bool)
			select f).ToArray();
		MethodInfo[] staticClientBoolMethods = (from m in sc.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
			where !m.IsStatic && m.ReturnType == typeof(bool) && m.GetParameters().Length == 0
			select m).ToArray();
		MethodInfo methodInfo = sc.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => m.IsStatic && m.ReturnType == typeof(void) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == sc);
		MethodInfo[] staticClientNoOpTaskMethods = (from m in sc.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
			where !m.IsStatic && m.ReturnType == typeof(Task) && m.GetCustomAttribute<AsyncStateMachineAttribute>() == null && m.GetParameters().Any((ParameterInfo p) => p.ParameterType == typeof(string))
			select m).ToArray();
		MethodInfo methodInfo2 = sc.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) =>
		{
			Type type12 = m.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
			return type12 != null && type12.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(TaskAwaiter<>) && f.FieldType.GetGenericArguments()[0].GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Count((FieldInfo rf) => !rf.IsStatic && rf.FieldType.Name == "RepeatedField`1") == 3);
		});
		Type br = source.FirstOrDefault((Type t) => !t.IsNested && t.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((ConstructorInfo c) => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == sc) && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Count((FieldInfo f) => !f.IsStatic && f.FieldType == typeof(string)) >= 2) ?? throw new InvalidOperationException("BridgeSorter type not found");
		ConstructorInfo bridgeSorterCtor = br.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).First((ConstructorInfo c) => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == sc);
		FieldInfo bridgeSorterStaticClientField = br.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).First((FieldInfo f) => !f.IsStatic && f.FieldType == sc);
		FieldInfo[] bridgeSorterStringFields = (from f in br.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
			where !f.IsStatic && f.FieldType == typeof(string)
			select f).ToArray();
		MethodInfo[] bridgeSorterBoolMethods = (from m in br.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
			where !m.IsStatic && m.ReturnType == typeof(bool) && m.GetParameters().Length == 0
			select m).ToArray();
		MethodInfo methodInfo3 = br.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => !m.IsStatic && m.ReturnType == typeof(string) && m.GetParameters().Length == 0);
		Type mon = source.FirstOrDefault((Type t) => !t.IsNested && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => f.FieldType == sc) && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => f.FieldType == br) && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => f.FieldType == typeof(Task)) && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(ConcurrentDictionary<, >) && f.FieldType.GetGenericArguments()[1].Name.StartsWith("ValueTuple`2", StringComparison.Ordinal))) ?? throw new InvalidOperationException("Monitor type not found");
		FieldInfo fieldInfo3 = mon.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).First((FieldInfo f) => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(ConcurrentDictionary<, >) && f.FieldType.GetGenericArguments()[1].Name.StartsWith("ValueTuple`2", StringComparison.Ordinal));
		Type adm = fieldInfo3.FieldType.GetGenericArguments()[1].GetGenericArguments()[1];
		FieldInfo monitorSingletonField = mon.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => f.IsStatic && f.FieldType == mon);
		MethodInfo monitorInitMethod = mon.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => m.IsStatic && m.ReturnType == typeof(void) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == mon);
		FieldInfo monitorTaskField = mon.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => !f.IsStatic && f.FieldType == typeof(Task));
		FieldInfo monitorStaticClientField = mon.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => !f.IsStatic && f.FieldType == sc);
		FieldInfo monitorBridgeSorterField = mon.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => !f.IsStatic && f.FieldType == br);
		FieldInfo[] array = (from f in adm.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
			where !f.IsStatic && f.FieldType == typeof(string)
			select f).ToArray();
		FieldInfo admissionAllowedField = adm.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => !f.IsStatic && f.FieldType == typeof(bool));
		PropertyInfo property = adm.GetProperty("AccessKey", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		MethodInfo methodInfo4 = mon.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => !m.IsStatic && m.ReturnType == typeof(Task<bool>) && m.GetParameters().Any((ParameterInfo p) => p.ParameterType == typeof(TimeSpan)));
		MethodInfo methodInfo5 = mon.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => !m.IsStatic && m.ReturnType == typeof(Task<string>) && m.GetParameters().Any((ParameterInfo p) => p.ParameterType.Name == "accountConfig"));
		MethodInfo[] monitorTaskNoOpMethods = (from m in mon.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
			where !m.IsStatic && !m.IsSpecialName && m.ReturnType == typeof(Task) && (m.GetCustomAttribute<AsyncStateMachineAttribute>() != null || m.GetParameters().Any((ParameterInfo p) => p.ParameterType == typeof(string)))
			select m).ToArray();
		Type adp = source.FirstOrDefault((Type t) => !t.IsNested && t != mon && t.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((ConstructorInfo c) => c.GetParameters().Length == 2 && c.GetParameters()[0].ParameterType == sc && c.GetParameters()[1].ParameterType == br) && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => f.IsStatic && f.FieldType == t) && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(ConcurrentDictionary<, >))) ?? throw new InvalidOperationException("Adapter type not found");
		ConstructorInfo adapterCtor = adp.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).First((ConstructorInfo c) => c.GetParameters().Length == 2 && c.GetParameters()[0].ParameterType == sc && c.GetParameters()[1].ParameterType == br);
		MethodInfo adapterStaticSetter = adp.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((PropertyInfo p) => p.PropertyType == adp && (p.SetMethod?.IsStatic ?? false))?.SetMethod ?? adp.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => m.IsStatic && m.ReturnType == typeof(void) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == adp);
		FieldInfo adapterStaticBackingField = adp.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => f.IsStatic && f.FieldType == adp);
		FieldInfo adapterDictField = adp.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(ConcurrentDictionary<, >));
		SingletonWiring[] clientBridgeSingletons = source.Where((Type t) => !t.IsNested && t != mon && t != adp && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => f.IsStatic && f.FieldType == t) && t.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((ConstructorInfo c) =>
		{
			ParameterInfo[] parameters = c.GetParameters();
			return parameters.Length == 2 && parameters[0].ParameterType == sc && parameters[1].ParameterType == br;
		})).Select((Type t) =>
		{
			ConstructorInfo ctor = t.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).First((ConstructorInfo c) =>
			{
				ParameterInfo[] parameters = c.GetParameters();
				return parameters.Length == 2 && parameters[0].ParameterType == sc && parameters[1].ParameterType == br;
			});
			MethodInfo staticSetter = t.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((PropertyInfo p) => p.PropertyType == t && (p.SetMethod?.IsStatic ?? false))?.SetMethod ?? t.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => m.IsStatic && m.ReturnType == typeof(void) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == t);
			FieldInfo staticBackingField = t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => f.IsStatic && f.FieldType == t);
			return new SingletonWiring(t, ctor, staticSetter, staticBackingField);
		}).ToArray();
		Type type2 = source.FirstOrDefault((Type t) => t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => f.IsStatic && f.FieldType == typeof(ConcurrentDictionary<string, byte[]>)) && t.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((PropertyInfo p) => p.Name == "Locked")) ?? throw new InvalidOperationException("KeyStore type not found");
		FieldInfo keyStoreMapField = type2.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).First((FieldInfo f) => f.IsStatic && f.FieldType == typeof(ConcurrentDictionary<string, byte[]>));
		MethodInfo methodInfo6 = type2.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => m.IsStatic && m.Name == "Set" && m.GetParameters().Any((ParameterInfo p) => p.ParameterType == typeof(string)) && m.GetParameters().Any((ParameterInfo p) => p.ParameterType == typeof(byte[])));
		MethodInfo methodInfo7 = type2.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => m.IsStatic && m.Name == "Store" && m.GetParameters().Any((ParameterInfo p) => p.ParameterType == typeof(string)) && m.GetParameters().Any((ParameterInfo p) => p.ParameterType == typeof(byte[])));
		Type lic = source.FirstOrDefault((Type t) => !t.IsNested && t.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((ConstructorInfo c) => c.GetParameters().Length == 0) && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Count((FieldInfo f) => !f.IsStatic && f.FieldType == typeof(DateTime)) == 1 && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => f.IsStatic && f.FieldType == t) && t.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((PropertyInfo p) => p.Name == "State") && t.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((MethodInfo m) => m.IsStatic && m.ReturnType == t && m.GetParameters().Length == 0));
		Type obs = source.FirstOrDefault((Type t) => !t.IsNested && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Count((FieldInfo f) => !f.IsStatic && f.FieldType == syncCacheType) >= 3 && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Count((FieldInfo f) => !f.IsStatic && f.FieldType == typeof(string)) == 2 && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => !f.IsStatic && f.FieldType.IsEnum));
		Type finder = ((obs == null) ? null : source.FirstOrDefault((Type t) => !t.IsNested && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => !f.IsStatic && f.FieldType == obs) && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Count((FieldInfo f) => !f.IsStatic && f.FieldType == syncCacheType) >= 2 && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => f.IsStatic && f.FieldType == t)));
		MethodInfo methodInfo8 = cont.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => !m.IsStatic && m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>) && m.GetParameters().Any((ParameterInfo p) => p.ParameterType == mon) && m.GetParameters().Any((ParameterInfo p) => p.ParameterType == typeof(string)));
		MethodInfo methodInfo9 = cont.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => !m.IsStatic && m.ReturnType == typeof(Task<bool>) && m.GetParameters().Length == 0 && m.GetCustomAttribute<AsyncStateMachineAttribute>() != null);
		MethodInfo methodInfo10 = cont.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => m.IsStatic && m.ReturnType == typeof(Task<string>) && m.GetParameters().Any((ParameterInfo p) => p.ParameterType == mon) && m.GetParameters().Any((ParameterInfo p) => p.ParameterType == typeof(string)) && m.GetParameters().Any((ParameterInfo p) => p.ParameterType == typeof(CancellationToken)));
		Type type3 = cont.GetNestedTypes(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((Type nt) => typeof(IAsyncStateMachine).IsAssignableFrom(nt) && nt.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => f.FieldType == cont) && nt.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => f.FieldType == adm));
		FieldInfo loginStateMachineBuilderField = type3?.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => f.FieldType.Name.StartsWith("AsyncTaskMethodBuilder", StringComparison.Ordinal));
		Type type4 = source.FirstOrDefault((Type t) => !t.IsNested && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => !f.IsStatic && f.FieldType == cont) && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => !f.IsStatic && f.FieldType.Name == "RallyData") && t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((FieldInfo f) => !f.IsStatic && f.FieldType.Name == "CargoShipData"));
		MethodInfo methodInfo11 = type4?.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) =>
		{
			if (m.IsStatic || m.ReturnType != typeof(Task))
			{
				return false;
			}
			ParameterInfo[] parameters = m.GetParameters();
			return parameters.Length == 3 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(string) && parameters[2].ParameterType == typeof(string);
		});
		Type type5 = source.FirstOrDefault((Type t) => t.Name == "CmdBase");
		MethodInfo cmdBaseInitMethod = type5?.GetMethod("Init", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		Type gameVersionType = source.FirstOrDefault((Type t) => t.Name == "GameVersion");
		Type type6 = source.FirstOrDefault((Type t) => t.FullName == "LordsMobileBot.Forms.ThirdParty.iggLogin") ?? source.FirstOrDefault((Type t) => t.Name == "iggLogin");
		MethodInfo methodInfo12 = type6?.GetMethod("PrepEntryAsync", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		Type type7 = source.FirstOrDefault((Type t) => t.FullName == "LordsMobileBot.AppClasses.AccountManager.GameAccount") ?? source.FirstOrDefault((Type t) => t.Name == "GameAccount");
		MethodInfo methodInfo13 = type7?.GetMethod("AddSessionData", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		MethodInfo methodInfo14 = type7?.GetMethod("WriteAccount", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		Type type8 = source.FirstOrDefault((Type t) => t.FullName == "LordsMobileBot.Learning.PredictorDriverTask") ?? source.FirstOrDefault((Type t) => t.Name == "PredictorDriverTask");
		MethodInfo methodInfo15 = type8?.GetMethod("DisableContext", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		FieldInfo sepMapField = null;
		FieldInfo compDicField = null;
		FieldInfo resDicField = null;
		FieldInfo allocIdField = null;
		Type allocType = null;
		Type type9 = source.FirstOrDefault((Type t) =>
		{
			if (t.IsNested || t.IsEnum || t.IsInterface)
			{
				return false;
			}
			FieldInfo[] fields = t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			if (fields.FirstOrDefault((FieldInfo f) => !f.IsStatic && f.FieldType == typeof(Dictionary<ushort, long>)) == null)
			{
				return false;
			}
			FieldInfo fieldInfo4 = fields.FirstOrDefault((FieldInfo f) => !f.IsStatic && f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(ConcurrentDictionary<, >) && f.FieldType.GetGenericArguments()[0] == typeof(ushort));
			if (fieldInfo4 == null)
			{
				return false;
			}
			FieldInfo fieldInfo5 = fields.FirstOrDefault((FieldInfo f) =>
			{
				if (f.IsStatic || !f.FieldType.IsGenericType)
				{
					return false;
				}
				if (f.FieldType.GetGenericTypeDefinition() != typeof(Dictionary<, >))
				{
					return false;
				}
				Type[] genericArguments = f.FieldType.GetGenericArguments();
				if (genericArguments[0] != typeof(ushort) || !genericArguments[1].IsClass || genericArguments[1] == typeof(string))
				{
					return false;
				}
				FieldInfo[] fields2 = genericArguments[1].GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				return fields2.Count((FieldInfo af) => !af.IsStatic && af.FieldType == typeof(ushort)) == 1 && fields2.Count((FieldInfo af) => !af.IsStatic && af.FieldType == typeof(uint)) == 1 && fields2.Count((FieldInfo af) => !af.IsStatic && af.FieldType == typeof(long)) == 1;
			});
			FieldInfo fieldInfo6 = fields.FirstOrDefault((FieldInfo f) => !f.IsStatic && f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<, >) && f.FieldType.GetGenericArguments()[0] == typeof(int));
			if (fieldInfo5 == null || fieldInfo6 == null)
			{
				return false;
			}
			sepMapField = fieldInfo5;
			compDicField = fieldInfo6;
			resDicField = fieldInfo4;
			allocType = fieldInfo5.FieldType.GetGenericArguments()[1];
			allocIdField = allocType.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo af) => !af.IsStatic && af.FieldType == typeof(ushort));
			return allocIdField != null;
		});
		MethodInfo methodInfo16 = type9?.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) =>
		{
			if (!m.IsStatic && m.ReturnType == allocType)
			{
				ParameterInfo[] parameters = m.GetParameters();
				if (parameters != null && parameters.Length == 1)
				{
					return parameters[0].ParameterType == typeof(ushort);
				}
			}
			return false;
		});
		MethodInfo methodInfo17 = type9?.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => !m.IsStatic && m.ReturnType == typeof(void) && m.GetParameters().Length == 0 && m.Name == "WriteGeneralChooser") ?? type9?.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where((MethodInfo m) => !m.IsStatic && m.ReturnType == typeof(void) && m.GetParameters().Length == 0).OrderByDescending((MethodInfo m) => m.MetadataToken)
			.FirstOrDefault();
		MethodInfo method = type.GetMethod("createNewAccountToolStripMenuItem_Click", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		MethodInfo awBulkCreate = type.GetMethod("bulkCreateAccountsToolStripMenuItem_Click", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		MethodInfo methodInfo18 = type7?.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => m.Name == "CreateNewAccount" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(bool));
		MethodInfo gaStartCreated = type7?.GetMethod("StartCreatedAccount", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		Type bulkType = source.FirstOrDefault((Type t) => t.Name == "bulkCreateAccountForm");
		MethodInfo bulkGetProxy = bulkType?.GetMethod("getProxy", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		MethodInfo bulkCheckProxy = bulkType?.GetMethod("checkProxy", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		MethodInfo bulkCanPing = bulkType?.GetMethod("canPing", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		MethodInfo bulkProxyWorker = bulkType?.GetMethod("proxyWorker_DoWork", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		MethodInfo bulkValidateProxy = bulkType?.GetMethod("validateProxy", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		MethodInfo bulkRowCellStyle = bulkType?.GetMethod("ProxyGrid_RowCellStyle", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) 
			?? bulkType?.GetMethod("proxyGrid_RowCellStyle", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		Type type10 = source.FirstOrDefault((Type t) => t.FullName == "LordsMobileBot.Search.RoleRecordFinder") 
			?? source.FirstOrDefault((Type t) => t.Name == "RoleRecordFinder")
			?? source.FirstOrDefault((Type t) => t.FullName == "LordsMobileBot.Mapping.CustomInterpreterMapper")
			?? source.FirstOrDefault((Type t) => t.Name == "CustomInterpreterMapper")
			?? clientBridgeSingletons.Select((SingletonWiring s) => s.Type).FirstOrDefault((Type t) => t.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any((MethodInfo m) => m.ReturnType == typeof(bool) && m.GetParameters().Length == 1 && (m.GetParameters()[0].ParameterType == typeof(long) || m.GetParameters()[0].ParameterType == typeof(string))));
		MethodInfo methodInfo19 = type10?.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => m.ReturnType == typeof(bool) && m.GetParameters().Length == 1 && (m.GetParameters()[0].ParameterType == typeof(long) || m.GetParameters()[0].ParameterType == typeof(string)));
		Type type11 = source.FirstOrDefault((Type t) => t.FullName == "LordsMobileBot.Roles.SegmentedCollectionRole") ?? source.FirstOrDefault((Type t) => t.Name == "SegmentedCollectionRole");
		MethodInfo methodInfo20 = type11?.GetMethod("AssignLogicalUserWatcher", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		MethodInfo methodInfo21 = type11?.GetMethod("AssignLiteralWatcherSharer", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		MethodInfo methodInfo22 = null;

		if (methodInfo20 == null)
		{
			foreach (Type t in source)
			{
				foreach (MethodInfo m in t.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
				{
					ParameterInfo[] p = m.GetParameters();
					if (p.Length == 7 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(string) &&
						p[2].ParameterType == typeof(bool) && p[3].ParameterType == typeof(bool) &&
						p[4].ParameterType == typeof(System.Windows.Forms.MessageBoxButtons) &&
						p[5].ParameterType == typeof(System.Windows.Forms.MessageBoxIcon) &&
						p[6].ParameterType == typeof(System.Windows.Forms.Control))
					{
						type11 = t;
						methodInfo20 = m;
						break;
					}
				}
				if (methodInfo20 != null) break;
			}
		}

		if (type11 != null)
		{
			if (methodInfo21 == null)
			{
				foreach (MethodInfo m2 in type11.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
				{
					ParameterInfo[] p2 = m2.GetParameters();
					if (p2.Length == 4 && p2[0].ParameterType == typeof(string) && p2[1].ParameterType == typeof(string) &&
						p2[2].ParameterType == typeof(System.Windows.Forms.MessageBoxIcon) && p2[3].ParameterType == typeof(bool) &&
						m2.ReturnType == typeof(System.Windows.Forms.DialogResult))
					{
						methodInfo21 = m2;
						break;
					}
				}
			}

			foreach (MethodInfo m3 in type11.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
			{
				ParameterInfo[] p3 = m3.GetParameters();
				if (p3.Length == 3 && p3[0].ParameterType == typeof(string) && p3[1].ParameterType == typeof(string) &&
					p3[2].ParameterType == typeof(System.Windows.Forms.MessageBoxIcon) &&
					m3.ReturnType == typeof(System.Windows.Forms.DialogResult))
				{
					methodInfo22 = m3;
					break;
				}
			}
		}
		MethodInfo baseDirGetter = null;
		foreach (Type t in source)
		{
			foreach (MethodInfo m in t.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
			{
				if (m.ReturnType == typeof(string) && m.GetParameters().Length == 0 &&
					(m.Name == "FindExtractor" || m.Name == "IdentifySymbolicBufferSorter"))
				{
					baseDirGetter = m;
					break;
				}
			}
			if (baseDirGetter != null) break;
		}

		MethodInfo tagNewValue = null;
		Type tagType = null;
		foreach (Type t in source)
		{
			foreach (MethodInfo m in t.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
			{
				if (m.ReturnType == typeof(HttpClient))
				{
					ParameterInfo[] p = m.GetParameters();
					if (p.Length == 3 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(bool) && p[2].ParameterType == typeof(bool))
					{
						tagType = t;
						tagNewValue = m;
						break;
					}
				}
			}
			if (tagNewValue != null) break;
		}
		int resolvedCount = 0;
		Count(type);
		Count(cont);
		Count(ciw);
		Count(sc);
		Count(parameterType);
		Count(methodInfo);
		Count(methodInfo2);
		Count(br);
		Count(methodInfo3);
		Count(mon);
		Count(adm);
		Count(methodInfo4);
		Count(methodInfo5);
		Count(adp);
		Count(type2);
		Count(methodInfo6);
		Count(methodInfo7);
		Count(lic);
		Count(obs);
		Count(finder);
		Count(methodInfo8);
		Count(methodInfo9);
		Count(methodInfo10);
		Count(type3);
		Count(type4);
		Count(methodInfo11);
		Count(methodInfo12);
		Count(type7);
		Count(methodInfo13);
		Count(methodInfo14);
		Count(type8);
		Count(methodInfo15);
		Count(type9);
		Count(methodInfo16);
		Count(methodInfo17);
		Count(method);
		Count(methodInfo18);
		Count(type10);
		Count(methodInfo19);
		Count(type11);
		Count(methodInfo20);
		Count(methodInfo21);
		Count(methodInfo22);
		Count(awBulkCreate);
		Count(gaStartCreated);
		Count(bulkType);
		Count(bulkGetProxy);
		Count(bulkCheckProxy);
		Count(bulkCanPing);
		Count(bulkProxyWorker);
		Count(bulkValidateProxy);
		Count(bulkRowCellStyle);
		Count(baseDirGetter);
		Count(tagType);
		Count(tagNewValue);
		return new BotSchema
		{
			BotAssembly = asm,
			Version = asm.GetName().Version,
			AccountWindowType = type,
			RunningAccountsField = fieldInfo,
			HasServerConnField = field,
			GameLangIdField = fieldInfo2,
			SyncCacheType = syncCacheType,
			SyncCacheCtorLong = syncCacheCtorLong,
			GameLangEnumType = fieldType,
			GameLangEngValue = gameLangEngValue,
			ContainerType = cont,
			ContainerGBSettingsField = containerGBSettingsField,
			ContainerMGSettingsField = containerMGSettingsField,
			ContainerCoordArrayField = containerCoordArrayField,
			ContainerAccRecordField = containerAccRecordField,
			CallInvokerWrapperType = ciw,
			CallInvokerField = callInvokerField,
			StaticClientType = sc,
			EndpointListHolderType = parameterType,
			EndpointListField = endpointListField,
			StaticClientCtor = constructorInfo,
			StaticClientWrapperFields = staticClientWrapperFields,
			StaticClientBoolFields = staticClientBoolFields,
			StaticClientBoolMethods = staticClientBoolMethods,
			StaticClientQueryVerifierMethod = methodInfo,
			StaticClientNoOpTaskMethods = staticClientNoOpTaskMethods,
			StaticClientGetIggHostsMethod = methodInfo2,
			BridgeSorterType = br,
			BridgeSorterCtor = bridgeSorterCtor,
			BridgeSorterStaticClientField = bridgeSorterStaticClientField,
			BridgeSorterStringFields = bridgeSorterStringFields,
			BridgeSorterBoolMethods = bridgeSorterBoolMethods,
			BridgeSorterProfileMethod = methodInfo3,
			AdapterType = adp,
			AdapterCtor = adapterCtor,
			AdapterStaticSetter = adapterStaticSetter,
			AdapterStaticBackingField = adapterStaticBackingField,
			AdapterDictField = adapterDictField,
			KeyStoreType = type2,
			KeyStoreMapField = keyStoreMapField,
			KeyStoreSetMethod = methodInfo6,
			KeyStoreStoreMethod = methodInfo7,
			MonitorType = mon,
			MonitorSingletonField = monitorSingletonField,
			MonitorInitMethod = monitorInitMethod,
			MonitorDicField = fieldInfo3,
			MonitorTaskField = monitorTaskField,
			MonitorStaticClientField = monitorStaticClientField,
			MonitorBridgeSorterField = monitorBridgeSorterField,
			AdmissionType = adm,
			AdmissionIdField = ((array.Length != 0) ? array[0] : null),
			AdmissionAllowedField = admissionAllowedField,
			AdmissionNoteField = ((array.Length > 1) ? array[1] : null),
			AdmissionAccessKeyProp = property,
			MonitorBoolDeciderMethod = methodInfo4,
			MonitorGetKeyMethod = methodInfo5,
			MonitorTaskNoOpMethods = monitorTaskNoOpMethods,
			LicenseStateType = lic,
			LicenseStateEnumField = lic?.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => !f.IsStatic && f.FieldType.IsEnum),
			LicenseStateTimeField = lic?.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => !f.IsStatic && f.FieldType == typeof(DateTime)),
			LicenseStateSingletonField = lic?.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => f.IsStatic && f.FieldType == lic),
			LicenseStateSingletonGetter = lic?.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => m.IsStatic && m.ReturnType == lic && m.GetParameters().Length == 0),
			LicenseStateSingletonSetter = lic?.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => m.IsStatic && m.ReturnType == typeof(void) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == lic),
			LicenseStateBoolMethods = (lic?.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where((MethodInfo m) => !m.IsStatic && m.ReturnType == typeof(bool) && m.GetParameters().Length == 0).ToArray() ?? Array.Empty<MethodInfo>()),
			ObserverType = obs,
			DistributorFinderType = finder,
			DistributorFinderSingletonGetter = finder?.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => m.IsStatic && m.ReturnType == finder && m.GetParameters().Length == 0),
			DistributorFinderSingletonField = finder?.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((FieldInfo f) => f.IsStatic && f.FieldType == finder),
			LoginReplaceControllerMethod = methodInfo8,
			LoginKillAutomatedStateMethod = methodInfo9,
			LoginConsumeIterableMethod = methodInfo10,
			LoginStateMachineType = type3,
			LoginStateMachineBuilderField = loginStateMachineBuilderField,
			SegmentedQueueType = type4,
			SegmentedQueueDequeueMethod = methodInfo11,
			CmdBaseType = type5,
			CmdBaseInitMethod = cmdBaseInitMethod,
			GameVersionType = gameVersionType,
			IggLoginType = type6,
			IggLoginPrepEntryMethod = methodInfo12,
			GameAccountType = type7,
			GameAccountAddSessionDataMethod = methodInfo13,
			GameAccountWriteAccountMethod = methodInfo14,
			PredictorDriverTaskType = type8,
			PredictorDriverTaskDisableContextMethod = methodInfo15,
			ClientBridgeSingletons = clientBridgeSingletons,
			AccountWindowCreateNewAccountMethod = method,
			AccountWindowBulkCreateAccountsMethod = awBulkCreate,
			GameAccountCreateNewAccountMethod = methodInfo18,
			GameAccountStartCreatedAccountMethod = gaStartCreated,
			BulkCreateAccountFormType = bulkType,
			BulkCreateAccountGetProxyMethod = bulkGetProxy,
			BulkCreateAccountCheckProxyMethod = bulkCheckProxy,
			BulkCreateAccountCanPingMethod = bulkCanPing,
			BulkCreateAccountProxyWorkerDoWorkMethod = bulkProxyWorker,
			BulkCreateAccountValidateProxyMethod = bulkValidateProxy,
			BulkCreateAccountRowCellStyleMethod = bulkRowCellStyle,
			RoleRecordFinderType = type10,
			RoleRecordFinderReportPassiveEventMethod = methodInfo19,
			StreamCompressorType = type9,
			AllocatorFieldType = allocType,
			AllocatorIdField = allocIdField,
			SeparatedValueMapField = sepMapField,
			CompressorFieldDicField = compDicField,
			ResolverServiceStreamDicField = resDicField,
			ResumeInitializerMethod = methodInfo16,
			WriteGeneralChooserMethod = methodInfo17,
			SegmentedCollectionRoleType = type11,
			AssignLogicalUserWatcherMethod = methodInfo20,
			AssignLiteralWatcherSharerMethod = methodInfo21,
			StartSharerMethod = methodInfo22,
			BaseDirectoryGetterMethod = baseDirGetter,
			ConfigurableReporterTagType = tagType,
			ConfigurableReporterTagNewValueMethod = tagNewValue,
			ResolvedFeatureCount = resolvedCount
		};
		void Count(object? o)
		{
			if (o != null)
			{
				resolvedCount++;
			}
		}
	}

	public static object? CreateSyncCache(BotSchema schema, long slots)
	{
		return schema.SyncCacheCtorLong.Invoke(new object[1] { slots });
	}
}
