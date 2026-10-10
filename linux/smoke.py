#!/usr/bin/python3
import argparse
import json
import os
from pathlib import Path
import sys
import tempfile


def main():
    parser = argparse.ArgumentParser(description='Check an installed GUI without device or network access')
    parser.add_argument('application', type=Path)
    parser.add_argument('--expected-version')
    args = parser.parse_args()
    sys.dont_write_bytecode = True
    application = args.application.resolve()
    expected_modules = {'momentum_control': application / 'momentum_control/__init__.py',
                        'momentum_control.gui': application / 'momentum_control/gui.py'}
    for name, path in expected_modules.items():
        if not path.is_file() or path.resolve() != path:
            raise RuntimeError(f'Installed module is missing or outside the application: {name}')
    sys.path.insert(0, str(application))
    os.environ['QT_NO_GLIB'] = '1'
    os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
    from PySide6.QtCore import QTimer
    from PySide6.QtWidgets import QApplication
    import momentum_control
    from momentum_control import gui

    for module in (momentum_control, gui):
        origin = getattr(module, '__file__', None)
        if not origin or Path(origin).resolve() != expected_modules[module.__name__]:
            raise RuntimeError(f'Imported module is outside the application: {module.__name__}')
    __version__ = momentum_control.__version__
    MainWindow = gui.MainWindow

    if args.expected_version is not None and __version__ != args.expected_version:
        raise RuntimeError('Installed version does not match the archive')
    result = {'version': __version__, 'event_loop': False, 'visible': False,
              'discovered': False, 'worker_stopped': False, 'external_calls': 0, 'timeout': False}

    def forbidden(*args, **kwargs):
        result['external_calls'] += 1
        raise RuntimeError('Unexpected external operation during GUI smoke check')

    def discover(cancelled=None):
        result['discovered'] = True
        return []

    app = QApplication([])
    with tempfile.TemporaryDirectory(prefix='momentum-gui-smoke-') as temporary:
        directory = Path(temporary)
        window = MainWindow(discover=discover, client_factory=forbidden, config_root=directory / 'config',
                            autostart_path=directory / 'autostart/app.desktop',
                            launcher_path=directory / 'bin/momentum-control',
                            release_checker=forbidden, location_provider_factory=forbidden,
                            tray_available=lambda: False)
        timer = QTimer(window)
        deadline = QTimer(window)
        deadline.setSingleShot(True)

        def finish():
            timer.stop()
            window.force_quit = True
            window.close()

        def tick():
            result['event_loop'] = True
            result['visible'] = window.isVisible()
            if not result['visible'] or (window.discovered_once and not window.busy):
                finish()

        def timed_out():
            result['timeout'] = True
            finish()

        timer.timeout.connect(tick)
        deadline.timeout.connect(timed_out)
        window.show()
        timer.start(25)
        deadline.start(5000)
        code = app.exec()
        result['worker_stopped'] = not window.thread.isRunning()
        deadline.stop()
        assert not (directory / 'autostart/app.desktop').exists(), 'Unexpected autostart entry'
        assert code == 0 and all(result[key] for key in ('event_loop', 'visible', 'discovered', 'worker_stopped'))
        assert not result['external_calls'] and not result['timeout']
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
