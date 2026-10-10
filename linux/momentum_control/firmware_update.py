from .dongle import _proxy
from .errors import UpdateError


def apply_update(plan, progress, cancelled=None):
    return _proxy.apply(plan, progress, cancelled)
