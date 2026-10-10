import unittest
from momentum_control.models import Device


class BackendProxyTests(unittest.TestCase):
    def test_setting_and_composite_mode_forward_one_typed_operation(self):
        from momentum_control.backend import BluetoothClient
        calls=[]
        value=dict(model='M5AEBT',firmware='6.21.5',battery=80,settings={'touch_controls':True},eq_bands=[0],eq_frequencies=[100],writable=['touch_controls'],notes=[],choices={},device_name=None,charging=None,wear_state=None,personalization_state=None,peers=[],max_connections=None,peers_available=False,eq_range=[-6,6],verified_session=True)
        class Lease:
            def __init__(self,*a,**k): pass
            def connect(self): pass
            def fileno(self): return 42
            def pump_idle(self): pass
            def close(self): calls.append('lease-close')
            def cancel(self): pass
        class Process:
            def __init__(self,*a,**k): pass
            def call(self,op,args,**kwargs): calls.append((op,args)); return value
            def close(self): calls.append('process-close')
            def cancel(self): pass
        device=Device('00:11:22:33:44:55','Headset','/org/bluez/hci0/dev_00_11_22_33_44_55',True)
        client=BluetoothClient(device,_lease_factory=Lease,_process_factory=Process)
        client.connect()
        self.assertTrue(client.set_value('touch_controls',True).settings['touch_controls'])
        client.set_noise_mode('transparency')
        self.assertEqual(calls,[('set-boolean',{'control':'touch_controls','value':True}),('noise-mode',{'mode':'transparency'})])
        client.close()
        self.assertEqual(calls[-2:],['process-close','lease-close'])

    def test_invalid_dto_clears_session_and_reaps_before_lease_release(self):
        from momentum_control.backend import BluetoothClient
        from momentum_control.errors import ControlError
        calls=[]
        client=BluetoothClient(Device('00:11:22:33:44:55','Headset','/org/bluez/hci0/dev_00_11_22_33_44_55',True))
        class Process:
            def call(self,*a,**k): return {'model':'forged'}
            def close(self): calls.append('process')
        class Lease:
            def close(self): calls.append('lease')
        client._process=Process(); client._lease=Lease()
        with self.assertRaises(ControlError): client.snapshot()
        self.assertEqual(calls,['process','lease'])
        self.assertIsNone(client._cached_snapshot)
