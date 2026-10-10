from .dongle import _proxy
from .errors import FirmwareError
from .models import UpdatePlan


def check_for_update(device, cancelled=None):
    return _proxy.check(device, cancelled)
