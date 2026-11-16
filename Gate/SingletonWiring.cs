using System;
using System.Reflection;

namespace LordsBotGate;

public sealed record SingletonWiring(Type Type, ConstructorInfo Ctor, MethodInfo? StaticSetter, FieldInfo? StaticBackingField);
