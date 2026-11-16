using System;
using System.Reflection;

namespace LordsBotGate;

public sealed class BotSchema
{
	public const BindingFlags AllDeclared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

	public const BindingFlags AllFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

	public Assembly BotAssembly { get; init; }

	public Version? Version { get; init; }

	public Type AccountWindowType { get; init; }

	public FieldInfo RunningAccountsField { get; init; }

	public FieldInfo? HasServerConnField { get; init; }

	public FieldInfo GameLangIdField { get; init; }

	public Type SyncCacheType { get; init; }

	public ConstructorInfo SyncCacheCtorLong { get; init; }

	public Type GameLangEnumType { get; init; }

	public object GameLangEngValue { get; init; }

	public Type ContainerType { get; init; }

	public FieldInfo ContainerGBSettingsField { get; init; }

	public FieldInfo ContainerMGSettingsField { get; init; }

	public FieldInfo ContainerCoordArrayField { get; init; }

	public FieldInfo ContainerAccRecordField { get; init; }

	public Type CallInvokerWrapperType { get; init; }

	public FieldInfo CallInvokerField { get; init; }

	public Type StaticClientType { get; init; }

	public Type EndpointListHolderType { get; init; }

	public FieldInfo EndpointListField { get; init; }

	public ConstructorInfo StaticClientCtor { get; init; }

	public FieldInfo[] StaticClientWrapperFields { get; init; } = Array.Empty<FieldInfo>();

	public FieldInfo[] StaticClientBoolFields { get; init; } = Array.Empty<FieldInfo>();

	public MethodInfo[] StaticClientBoolMethods { get; init; } = Array.Empty<MethodInfo>();

	public MethodInfo? StaticClientQueryVerifierMethod { get; init; }

	public MethodInfo[] StaticClientNoOpTaskMethods { get; init; } = Array.Empty<MethodInfo>();

	public MethodInfo? StaticClientGetIggHostsMethod { get; init; }

	public Type BridgeSorterType { get; init; }

	public ConstructorInfo BridgeSorterCtor { get; init; }

	public FieldInfo BridgeSorterStaticClientField { get; init; }

	public FieldInfo[] BridgeSorterStringFields { get; init; } = Array.Empty<FieldInfo>();

	public MethodInfo[] BridgeSorterBoolMethods { get; init; } = Array.Empty<MethodInfo>();

	public MethodInfo? BridgeSorterProfileMethod { get; init; }

	public Type AdapterType { get; init; }

	public ConstructorInfo AdapterCtor { get; init; }

	public MethodInfo? AdapterStaticSetter { get; init; }

	public FieldInfo? AdapterStaticBackingField { get; init; }

	public FieldInfo? AdapterDictField { get; init; }

	public SingletonWiring[] ClientBridgeSingletons { get; init; } = Array.Empty<SingletonWiring>();

	public Type KeyStoreType { get; init; }

	public FieldInfo KeyStoreMapField { get; init; }

	public MethodInfo? KeyStoreSetMethod { get; init; }

	public MethodInfo? KeyStoreStoreMethod { get; init; }

	public Type MonitorType { get; init; }

	public FieldInfo? MonitorSingletonField { get; init; }

	public MethodInfo? MonitorInitMethod { get; init; }

	public FieldInfo MonitorDicField { get; init; }

	public FieldInfo? MonitorTaskField { get; init; }

	public FieldInfo? MonitorStaticClientField { get; init; }

	public FieldInfo? MonitorBridgeSorterField { get; init; }

	public Type AdmissionType { get; init; }

	public FieldInfo? AdmissionIdField { get; init; }

	public FieldInfo? AdmissionAllowedField { get; init; }

	public FieldInfo? AdmissionNoteField { get; init; }

	public PropertyInfo? AdmissionAccessKeyProp { get; init; }

	public MethodInfo? MonitorBoolDeciderMethod { get; init; }

	public MethodInfo? MonitorGetKeyMethod { get; init; }

	public MethodInfo[] MonitorTaskNoOpMethods { get; init; } = Array.Empty<MethodInfo>();

	public Type? LicenseStateType { get; init; }

	public FieldInfo? LicenseStateEnumField { get; init; }

	public FieldInfo? LicenseStateTimeField { get; init; }

	public FieldInfo? LicenseStateSingletonField { get; init; }

	public MethodInfo? LicenseStateSingletonGetter { get; init; }

	public MethodInfo? LicenseStateSingletonSetter { get; init; }

	public MethodInfo[] LicenseStateBoolMethods { get; init; } = Array.Empty<MethodInfo>();

	public Type? ObserverType { get; init; }

	public Type? DistributorFinderType { get; init; }

	public MethodInfo? DistributorFinderSingletonGetter { get; init; }

	public FieldInfo? DistributorFinderSingletonField { get; init; }

	public MethodInfo? LoginReplaceControllerMethod { get; init; }

	public MethodInfo? LoginKillAutomatedStateMethod { get; init; }

	public MethodInfo? LoginConsumeIterableMethod { get; init; }

	public Type? LoginStateMachineType { get; init; }

	public FieldInfo? LoginStateMachineBuilderField { get; init; }

	public Type? SegmentedQueueType { get; init; }

	public MethodInfo? SegmentedQueueDequeueMethod { get; init; }

	public Type? CmdBaseType { get; init; }

	public MethodInfo? CmdBaseInitMethod { get; init; }

	public Type? GameVersionType { get; init; }

	public Type? IggLoginType { get; init; }

	public MethodInfo? IggLoginPrepEntryMethod { get; init; }

	public Type? GameAccountType { get; init; }

	public MethodInfo? GameAccountAddSessionDataMethod { get; init; }

	public MethodInfo? GameAccountWriteAccountMethod { get; init; }

	public Type? PredictorDriverTaskType { get; init; }

	public MethodInfo? PredictorDriverTaskDisableContextMethod { get; init; }

	public MethodInfo? AccountWindowCreateNewAccountMethod { get; init; }

	public MethodInfo? AccountWindowBulkCreateAccountsMethod { get; init; }

	public MethodInfo? GameAccountCreateNewAccountMethod { get; init; }

	public MethodInfo? GameAccountStartCreatedAccountMethod { get; init; }

	public Type? BulkCreateAccountFormType { get; init; }

	public MethodInfo? BulkCreateAccountGetProxyMethod { get; init; }

	public MethodInfo? BulkCreateAccountCheckProxyMethod { get; init; }

	public MethodInfo? BulkCreateAccountCanPingMethod { get; init; }

	public MethodInfo? BulkCreateAccountProxyWorkerDoWorkMethod { get; init; }

	public MethodInfo? BulkCreateAccountValidateProxyMethod { get; init; }

	public MethodInfo? BulkCreateAccountRowCellStyleMethod { get; init; }

	public Type? RoleRecordFinderType { get; init; }

	public MethodInfo? RoleRecordFinderReportPassiveEventMethod { get; init; }

	public Type? AccountCreationValidatorType { get; init; }

	public MethodInfo? AccountCreationValidatorMethod { get; init; }

	public Type? StreamCompressorType { get; init; }

	public Type? AllocatorFieldType { get; init; }

	public FieldInfo? AllocatorIdField { get; init; }

	public FieldInfo? SeparatedValueMapField { get; init; }

	public FieldInfo? CompressorFieldDicField { get; init; }

	public FieldInfo? ResolverServiceStreamDicField { get; init; }

	public MethodInfo? ResumeInitializerMethod { get; init; }

	public MethodInfo? WriteGeneralChooserMethod { get; init; }

	public Type? SegmentedCollectionRoleType { get; init; }

	public MethodInfo? AssignLogicalUserWatcherMethod { get; init; }

	public MethodInfo? AssignLiteralWatcherSharerMethod { get; init; }

	public MethodInfo? StartSharerMethod { get; init; }

	public MethodInfo? BaseDirectoryGetterMethod { get; init; }

	public Type? ConfigurableReporterTagType { get; init; }

	public MethodInfo? ConfigurableReporterTagNewValueMethod { get; init; }

	public int ResolvedFeatureCount { get; init; }
}
