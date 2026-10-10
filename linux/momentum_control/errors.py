class ControlError(RuntimeError):
    pass


class CoreOperationError(ControlError):
    def __init__(self, code):
        self.code = code
        super().__init__('Shared device core rejected operation: ' + code)


class CommandRejected(ControlError):
    pass


class DongleError(ControlError):
    pass


class DongleDisconnectedError(DongleError):
    pass


class FirmwareError(DongleError):
    pass


class UpdateError(FirmwareError):
    def __init__(self, stage, detail, recovery_required, interrupted=False):
        self.stage = stage
        self.recovery_required = recovery_required
        self.interrupted = interrupted
        super().__init__(f'Firmware update failed during {stage}: {detail}' + ('; recovery required' if recovery_required else ''))
