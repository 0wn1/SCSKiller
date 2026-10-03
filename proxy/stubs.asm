; Raw jmp forwarders to the system d3d12.dll; pointers are resolved in DllMain (proxy.cpp).
STUB MACRO name
    EXTERN real_&name:QWORD
    stub_&name PROC
        jmp QWORD PTR [real_&name]
    stub_&name ENDP
ENDM

.code
STUB D3D12CoreCreateLayeredDevice
STUB D3D12CoreGetLayeredDeviceSize
STUB D3D12CoreRegisterLayers
STUB D3D12CreateRootSignatureDeserializer
STUB D3D12CreateVersionedRootSignatureDeserializer
STUB D3D12DeviceRemovedExtendedData
STUB D3D12EnableExperimentalFeatures
STUB D3D12GetDebugInterface
STUB D3D12PIXEventsReplaceBlock
STUB D3D12PIXGetThreadInfo
STUB D3D12PIXNotifyWakeFromFenceSignal
STUB D3D12PIXReportCounter
STUB D3D12SerializeRootSignature
STUB D3D12SerializeVersionedRootSignature
STUB GetBehaviorValue
STUB SetAppCompatStringPointer
STUB Ordinal99
END
