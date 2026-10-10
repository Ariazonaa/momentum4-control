import contextlib
import io
import sys
import unittest
from unittest.mock import patch
from momentum_control import __main__ as entrypoint
from momentum_control import gui


class EntrypointTests(unittest.TestCase):
    def test_minimized_is_parsed_by_real_entrypoint_and_forwarded(self):
        with patch.object(sys, 'argv', ['momentum-control', '--minimized']), patch.object(gui, 'main', return_value=23) as launch, contextlib.redirect_stderr(io.StringIO()):
            try:
                result = entrypoint.main()
            except SystemExit as error:
                self.fail(f'Actual GUI entrypoint rejected --minimized: {error}')
        self.assertEqual(result, 23)
        self.assertEqual(launch.call_args.kwargs, {'minimized': True})
        self.assertEqual(launch.call_args.args, (['momentum-control'],))

    def test_help_does_not_import_or_start_gui(self):
        with patch.object(sys, 'argv', ['momentum-control', '--help']), patch.object(gui, 'main', side_effect=AssertionError('GUI started')), contextlib.redirect_stdout(io.StringIO()), self.assertRaises(SystemExit) as result:
            entrypoint.main()
        self.assertEqual(result.exception.code, 0)
