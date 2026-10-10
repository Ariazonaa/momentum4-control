import argparse
import sys


def main():
    parser = argparse.ArgumentParser(description='Momentum Control — native Linux Bluetooth headset settings')
    parser.add_argument('--minimized', action='store_true', help='Start hidden when an enabled system tray is available')
    args = parser.parse_args()
    try:
        import PySide6
        import dbus
        import gi
        from .gui import main as gui_main
    except ImportError as error:
        print(f'Missing dependency: {error}\n'
              '  Debian 13 / Ubuntu 25.04+: sudo apt install python3-pyside6.qtcore python3-pyside6.qtgui python3-pyside6.qtwidgets python3-dbus python3-gi\n'
              '  Ubuntu 24.04: sudo apt install python3-pip python3-dbus python3-gi libegl1 libgl1 libxkbcommon0 libfontconfig1\n'
              '    /usr/bin/python3 -m pip install --user --break-system-packages PySide6-Essentials\n'
              '  Fedora:        sudo dnf install python3-pyside6 python3-dbus python3-gobject\n'
              '  Arch:          sudo pacman -S pyside6 python-dbus python-gobject', file=sys.stderr)
        return 1
    return gui_main([sys.argv[0]], minimized=args.minimized)


if __name__ == '__main__':
    raise SystemExit(main())
