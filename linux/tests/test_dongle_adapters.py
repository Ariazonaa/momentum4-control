import threading
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from momentum_control import cli, dongle
from momentum_control.gui import DongleServices, Request, Worker


class AdapterTests(unittest.TestCase):
    def setUp(self):
        self.device=dongle.DongleDevice('/dev/hidraw10','/dev/hidraw11','/sys/devices/usb1/1-2','identity')

    def test_cli_cancelled_check_denies_before_child_or_hardware(self):
        cancelled=threading.Event();cancelled.set()
        with patch.object(dongle._proxy,'_process_factory',side_effect=AssertionError('Child access')), patch.object(dongle._proxy,'_adapter_factory',side_effect=AssertionError('Hardware access')):
            with self.assertRaisesRegex(RuntimeError,'cancelled'):
                cli.check_dongle_update(self.device,cancelled)

    def test_cli_apply_cancellation_denies_before_unprepared_plan(self):
        cancelled=threading.Event();cancelled.set()
        with self.assertRaisesRegex(RuntimeError,'cancelled'):
            cli.apply_dongle_update(SimpleNamespace(device=self.device),lambda *args:None,cancelled)

    def test_worker_apply_cancellation_is_callable_and_denies_before_child(self):
        worker=Worker(lambda cancelled=None:(),lambda *a,**k:None,DongleServices())
        results=[]
        worker.completed.connect(lambda request,result,error:results.append((result,error)))
        worker.shutdown.set()
        worker.execute(Request('dongle-update',0,self.device,payload=SimpleNamespace(device=self.device)))
        self.assertEqual(len(results),1);self.assertIsNone(results[0][0]);self.assertIn('cancelled',results[0][1])

    def test_worker_status_and_check_forward_callable_cancellation(self):
        worker=Worker(lambda cancelled=None:(),lambda *a,**k:None,DongleServices())
        from momentum_control import firmware
        for action,module,name in (('dongle-status',dongle,'read_status'),('dongle-check',firmware,'check_for_update')):
            observed=[]
            def consumer(device,cancelled=None):
                observed.append((device,callable(cancelled),cancelled()))
                return 'result'
            with self.subTest(action=action),patch.object(module,name,side_effect=consumer):
                worker.execute(Request(action,0,self.device))
                self.assertEqual(observed,[(self.device,True,False)])

    def test_cli_status_and_check_forward_event_as_callable(self):
        from momentum_control import firmware
        for adapter,module,name in ((cli.read_dongle_status,dongle,'read_status'),(cli.check_dongle_update,firmware,'check_for_update')):
            event=threading.Event();observed=[]
            def consumer(device,cancelled=None):
                observed.append((device,callable(cancelled),cancelled()))
                return 'result'
            with patch.object(module,name,side_effect=consumer):
                self.assertEqual(adapter(self.device,event),'result')
                self.assertEqual(observed,[(self.device,True,False)])

    def test_codec_adapters_cancel_before_child_or_lock_access(self):
        cancelled=threading.Event();cancelled.set()
        with patch.object(dongle._proxy,'_adapter_factory',side_effect=AssertionError('Hardware access')):
            with self.assertRaisesRegex(RuntimeError,'cancelled'):
                cli.set_dongle_codec(self.device,dongle.Codec.APTX_ADAPTIVE,cancelled)
            with self.assertRaisesRegex(RuntimeError,'cancelled'):
                DongleServices().codec(self.device,dongle.Codec.APTX_ADAPTIVE,cancelled.is_set)

    def test_foreign_or_reused_plan_denies_before_child(self):
        proxy=dongle.DongleProxy(_process_factory=lambda *a,**k:(_ for _ in ()).throw(AssertionError('Child access')))
        with self.assertRaisesRegex(RuntimeError,'stale|foreign'):
            proxy.apply(SimpleNamespace(device=self.device),lambda *args:None)

    def test_cli_codec_accepts_named_choice_and_reports_observed_codec(self):
        import contextlib
        import io
        import json
        output=io.StringIO()
        observed=SimpleNamespace(device=self.device,firmware='3.11.0',state=3,audio_mode=1,transport=1,codec_mask=4,supported_codec_mask=15,sample_bits=24,sample_rate=96000)
        with patch.object(cli,'discover_dongles',return_value=[self.device]),patch.object(cli,'set_dongle_codec',return_value=observed) as setter,contextlib.redirect_stdout(output):
            self.assertEqual(cli.main(['dongle-codec','aptx-adaptive','--json']),0)
        self.assertEqual(json.loads(output.getvalue())['codec'],'aptX Adaptive')
        self.assertIs(setter.call_args.args[1],dongle.Codec.APTX_ADAPTIVE)
        self.assertIsInstance(setter.call_args.kwargs['cancelled'],threading.Event)

    def test_cli_codec_numeric_mask_rejected_before_usb(self):
        import contextlib
        import io
        with patch.object(cli,'discover_dongles',side_effect=AssertionError('USB access')),contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as result: cli.main(['dongle-codec','4'])
        self.assertEqual(result.exception.code,2)

    def test_worker_eq_carries_submitted_frequency_topology_to_setter(self):
        calls=[]
        class Client:
            def set_value(self,name,value,band=None,expected_topology=None):
                calls.append((name,value,band,expected_topology));return 'verified'
            def close(self): pass
        worker=Worker(lambda cancelled=None:(),lambda *a,**k:None);worker.client=Client()
        results=[];worker.completed.connect(lambda request,result,error:results.append((result,error)))
        worker.execute(Request('write',0,name='eq',value=1.2,band=1,eq_topology=(90,325,1500)))
        self.assertEqual(calls,[('eq',1.2,1,(3,(90,325,1500)))]);self.assertEqual(results,[('verified','')])

    def test_unknown_apply_completion_preserves_recovery_required(self):
        from momentum_control.errors import ControlError, UpdateError
        from momentum_control.models import UpdatePlan, UpgradeVersion, FirmwareVersion
        proxy=dongle.DongleProxy()
        plan=UpdatePlan(self.device,UpgradeVersion(FirmwareVersion(3,11,0),1),FirmwareVersion(3,12,0),1,True,'Prepared')
        class Process:
            def call(self,*args,**kwargs): raise ControlError('Shared device core exited during operation')
            def close(self): pass
        proxy._process=Process();proxy._device=self.device;proxy._plan=plan
        with self.assertRaises(UpdateError) as failed:
            proxy.apply(plan,lambda *args:None)
        self.assertTrue(failed.exception.recovery_required)
        self.assertIn('unknown',str(failed.exception).lower())

    def test_typed_update_error_and_preflight_refusal_keep_authoritative_flags(self):
        from momentum_control.errors import CoreOperationError, UpdateError
        from momentum_control.models import UpdatePlan, UpgradeVersion, FirmwareVersion
        for original in (UpdateError('transfer','Reply lost',True,True),CoreOperationError('invalid')):
            proxy=dongle.DongleProxy()
            plan=UpdatePlan(self.device,UpgradeVersion(FirmwareVersion(3,11,0),1),FirmwareVersion(3,12,0),1,True,'Prepared')
            class Process:
                def call(self,*args,**kwargs): raise original
                def close(self): pass
            proxy._process=Process();proxy._device=self.device;proxy._plan=plan
            with self.subTest(error=type(original).__name__),self.assertRaises(UpdateError) as failed:
                proxy.apply(plan,lambda *args:None)
            if isinstance(original,UpdateError):
                self.assertIs(failed.exception,original)
                self.assertTrue(failed.exception.recovery_required);self.assertTrue(failed.exception.interrupted)
            else:
                self.assertEqual(failed.exception.stage,'preflight');self.assertFalse(failed.exception.recovery_required)
