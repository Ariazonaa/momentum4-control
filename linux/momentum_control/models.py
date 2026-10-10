from dataclasses import dataclass, field
import math


@dataclass(frozen=True)
class Device:
    address: str
    name: str
    path: str
    connected: bool


@dataclass(frozen=True)
class PeerDevice:
    index: int
    name: str
    connected: bool | None
    is_this_pc: bool
    priority: int = 0


@dataclass(frozen=True)
class Snapshot:
    model: str
    firmware: str
    battery: int | None
    settings: dict[str, bool | int]
    eq_bands: tuple[float, ...]
    eq_frequencies: tuple[int, ...]
    writable: frozenset[str]
    notes: tuple[str, ...]
    choices: dict[str, tuple[int, ...]] = field(default_factory=dict)
    device_name: str | None = None
    charging: bool | None = None
    wear_state: int | None = None
    personalization_state: int | None = None
    peers: tuple[PeerDevice, ...] = ()
    max_connections: int | None = None
    peers_available: bool = False
    eq_range: tuple[float, float] | None = None
    verified_session: bool = False


@dataclass(frozen=True)
class DongleDevice:
    control_path: str
    upgrade_path: str
    usb_path: str
    identity: str


_BOOLEAN_FIELDS = frozenset(('anc_enabled','transparency','touch_controls','smart_pause','on_head_detection',
    'auto_answer','comfort_call','bass_boost','pause_on_transparency','adaptive_anc','immersive_audio','head_tracking','battery_protection'))
_INTEGER_FIELDS = frozenset(('anc_strength','transparency_level','antiwind','audio_prompts','sidetone','auto_power_off','sound_mode'))


def snapshot_from_dict(value):
    from .errors import ControlError
    expected = {'model','firmware','battery','settings','eq_bands','eq_frequencies','eq_range','writable','notes','choices',
                'device_name','charging','wear_state','personalization_state','peers','max_connections','peers_available','verified_session'}
    try:
        if type(value) is not dict or set(value) != expected:
            raise ValueError('Snapshot fields')
        if type(value['model']) is not str or type(value['firmware']) is not str or len(value['model']) > 128 or len(value['firmware']) > 32:
            raise ValueError('Snapshot identity')
        for key in ('battery','wear_state','personalization_state','max_connections'):
            if value[key] is not None and type(value[key]) is not int:
                raise ValueError('Snapshot number')
        for key in ('charging',):
            if value[key] is not None and type(value[key]) is not bool:
                raise ValueError('Snapshot boolean')
        if any(type(value[key]) is not bool for key in ('peers_available','verified_session')):
            raise ValueError('Snapshot boolean')
        if value['device_name'] is not None and (type(value['device_name']) is not str or len(value['device_name']) > 128):
            raise ValueError('Snapshot name')
        settings = value['settings']
        if type(settings) is not dict or any(key not in _BOOLEAN_FIELDS | _INTEGER_FIELDS or type(item) is not (bool if key in _BOOLEAN_FIELDS else int) for key,item in settings.items()):
            raise ValueError('Snapshot settings')
        if type(value['eq_bands']) is not list or len(value['eq_bands']) > 16 or any(type(item) not in (int,float) or not math.isfinite(item) for item in value['eq_bands']):
            raise ValueError('Snapshot gains')
        if type(value['eq_frequencies']) is not list or len(value['eq_frequencies']) > 16 or any(type(item) is not int for item in value['eq_frequencies']):
            raise ValueError('Snapshot frequencies')
        if value['eq_range'] is not None and (type(value['eq_range']) is not list or len(value['eq_range']) != 2 or any(type(item) not in (int,float) or not math.isfinite(item) for item in value['eq_range'])):
            raise ValueError('Snapshot range')
        if type(value['writable']) is not list or len(value['writable']) > 23 or len(set(value['writable'])) != len(value['writable']) or any(item not in _BOOLEAN_FIELDS | _INTEGER_FIELDS | {'eq','rename','connect_peer'} for item in value['writable']):
            raise ValueError('Snapshot controls')
        if type(value['notes']) is not list or len(value['notes']) > 64 or any(type(item) is not str or len(item) > 512 for item in value['notes']):
            raise ValueError('Snapshot notes')
        if type(value['choices']) is not dict or any(key not in _INTEGER_FIELDS or type(items) is not list or len(items) > 32 or any(type(item) is not int for item in items) for key,items in value['choices'].items()):
            raise ValueError('Snapshot choices')
        if type(value['peers']) is not list or len(value['peers']) > 64:
            raise ValueError('Snapshot peers')
        peers=[]
        for peer in value['peers']:
            if type(peer) is not dict or set(peer) != {'index','name','connected','is_this_pc','priority'} or type(peer['index']) is not int or type(peer['priority']) is not int or type(peer['name']) is not str or len(peer['name']) > 128 or type(peer['is_this_pc']) is not bool or peer['connected'] is not None and type(peer['connected']) is not bool:
                raise ValueError('Snapshot peer')
            peers.append(PeerDevice(**peer))
        mapped = dict(value, settings=dict(settings), eq_bands=tuple(value['eq_bands']), eq_frequencies=tuple(value['eq_frequencies']),
                      eq_range=None if value['eq_range'] is None else tuple(value['eq_range']), writable=frozenset(value['writable']), notes=tuple(value['notes']),
                      choices={key:tuple(items) for key,items in value['choices'].items()}, peers=tuple(peers))
        return Snapshot(**mapped)
    except (ValueError,TypeError,KeyError) as error:
        raise ControlError('Shared device core snapshot protocol failure') from error


@dataclass(frozen=True, order=True)
class FirmwareVersion:
    major: int
    minor: int
    patch: int

    @classmethod
    def parse(cls, value):
        if type(value) is not str or len(value) > 32:
            raise ValueError('Invalid version presentation')
        parts = value.split('.')
        if len(parts) != 3 or any(not part.isascii() or not part.isdecimal() for part in parts):
            raise ValueError('Invalid version presentation')
        return cls(*(int(part) for part in parts))

    def __str__(self):
        return f'{self.major}.{self.minor}.{self.patch}'


@dataclass(frozen=True)
class DongleStatus:
    device: DongleDevice
    firmware: FirmwareVersion
    state: int
    audio_mode: int
    transport: int
    codec_mask: int
    sample_bits: int | None
    sample_rate: int | None
    supported_codec_mask: int = 0


@dataclass(frozen=True)
class UpgradeVersion:
    firmware: FirmwareVersion
    config: int


@dataclass(frozen=True)
class UpdatePlan:
    device: DongleDevice
    current: UpgradeVersion
    target: FirmwareVersion
    handle: int
    available: bool
    reason: str
    sha256: str | None = None
    image_size: int | None = None


def upgrade_from_dict(value):
    if type(value) is not dict or set(value) != {'firmware','config'} or type(value['config']) is not int:
        raise ValueError('Invalid upgrade version presentation')
    return UpgradeVersion(FirmwareVersion.parse(value['firmware']), value['config'])


def status_from_dict(device, value):
    expected = {'device','firmware','state','audio_mode','transport','codec_mask','sample_bits','sample_rate','supported_codec_mask'}
    if type(value) is not dict or set(value) != expected or any(type(value[key]) is not int for key in ('state','audio_mode','transport','codec_mask','supported_codec_mask')):
        raise ValueError('Invalid dongle status presentation')
    if any(value[key] is not None and type(value[key]) is not int for key in ('sample_bits','sample_rate')):
        raise ValueError('Invalid dongle quality presentation')
    metadata = value['device']
    if type(metadata) is not dict or set(metadata) != {'control_path','upgrade_path','usb_path','identity'} or any(type(item) is not str or len(item) > 1024 for item in metadata.values()):
        raise ValueError('Invalid observed dongle presentation')
    actual = DongleDevice(**metadata)
    if actual.usb_path != device.usb_path or actual.identity != device.identity:
        raise ValueError('Foreign observed dongle presentation')
    mapped = dict(value, firmware=FirmwareVersion.parse(value['firmware']))
    mapped.pop('device')
    return DongleStatus(device=actual, **mapped)


def plan_from_dict(device, value):
    expected = {'current','target','handle','available','reason','sha256','image_size'}
    if type(value) is not dict or set(value) != expected or type(value['handle']) is not int or value['handle'] <= 0 or type(value['available']) is not bool or type(value['reason']) is not str or len(value['reason']) > 512:
        raise ValueError('Invalid update plan presentation')
    if value['sha256'] is not None and (type(value['sha256']) is not str or len(value['sha256']) != 64):
        raise ValueError('Invalid update digest presentation')
    if value['image_size'] is not None and type(value['image_size']) is not int:
        raise ValueError('Invalid update size presentation')
    return UpdatePlan(device=device, **dict(value, current=upgrade_from_dict(value['current']), target=FirmwareVersion.parse(value['target'])))
