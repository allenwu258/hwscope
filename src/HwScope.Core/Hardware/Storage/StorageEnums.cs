namespace HwScope.Core.Hardware.Storage;

public enum StorageDataSource
{
    Unknown,
    Wmi,
    WindowsStorage,
    StorageApi,
    Nvme,
    AtaSmart,
    Scsi,
    StorageSpaces,
    Computed,
    Placeholder
}

public enum StorageProtocolKind
{
    Unknown,
    Nvme,
    Ata,
    Scsi,
    Sas,
    Sd,
    Ufs,
    Proprietary
}

public enum StorageBusKind
{
    Unknown,
    Scsi,
    Ata,
    Ssa,
    Fibre,
    Usb,
    Raid,
    ISCSI,
    Sas,
    Sata,
    Sd,
    Mmc,
    Virtual,
    FileBackedVirtual,
    Spaces,
    Nvme,
    Scm,
    Ufs
}

public enum StorageHealthStatus
{
    Good,
    Caution,
    Critical,
    Unknown,
    Unsupported
}

public enum StorageAttributeSeverity
{
    Normal,
    Information,
    Caution,
    Critical
}

public enum StorageErrorKind
{
    AccessDenied,
    DeviceNotFound,
    DeviceRemoved,
    UnsupportedBus,
    ProtocolPassThroughUnavailable,
    ControllerBlocked,
    MalformedResponse,
    Timeout,
    Cancelled,
    DriverError,
    Unknown
}
