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
        print(f'Missing dependency: {error}\nFedora/Nobara: sudo dnf install python3-pyside6 python3-dbus python3-gobject', file=sys.stderr)
        return 1
    return gui_main([sys.argv[0]], minimized=args.minimized)


if __name__ == '__main__':
    raise SystemExit(main())
