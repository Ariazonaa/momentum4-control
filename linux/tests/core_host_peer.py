import json
import os
from pathlib import Path
import shlex
import socket
import threading


def host_command():
    configured = os.environ.get('M4_TEST_CORE_COMMAND')
    return shlex.split(configured) if configured else [str(Path(__file__).resolve().parents[1] / 'core' / 'momentum-core-host')]


class HeadsetPeer:
    def __init__(self, model=None, *, profile="m4"):
        self.requests=[]
        self.writes=[]
        self.errors=[]
        self.touch_locked=False
        self.answers={}
        captures=Path(__file__).resolve().parents[2]/'tests'/'CapturedPackets'
        for file in sorted(captures.glob('*.json')):
            value=json.loads(file.read_text())
            steps=value.get('steps')
            if steps:
                for step in steps:
                    self.answers[bytes.fromhex(step['tx'])]=bytes.fromhex(step['rx'])
            elif 'exchange' in value:
                tx=[row for row in value['exchange'] if row['dir']=='TX']
                rx=[row for row in value['exchange'] if row['dir']=='RX']
                if len(tx)==len(rx)==1:
                    self.answers[bytes.fromhex(tx[0]['hex'])]=bytes.fromhex(rx[0]['hex'])
        if profile == 'm5':
            captured = {}
            for file in sorted((captures/'M5').glob('*.json')):
                value=json.loads(file.read_text())
                for exchange in value['exchanges']:
                    if 'tx' not in exchange and 'response' not in exchange:
                        continue
                    if 'tx' in exchange:
                        request=bytes.fromhex(exchange['tx']);response=bytes.fromhex(exchange['rx'])
                    else:
                        command=int(exchange['command'],16)
                        payload=bytes.fromhex(exchange.get('request',exchange.get('payload','')))
                        request=bytes((255,3,0,len(payload)))+bytes.fromhex('0495')+command.to_bytes(2,'big')+payload
                        response=self.reply(0x0495,command,bytes.fromhex(exchange['response']))
                    captured.setdefault(request,response)
            self.answers.update(captured)
        self.model=model
        self.stall=False
        self.large_peers=False
        self.parent, self.socket = socket.socketpair()
        self.thread=threading.Thread(target=self.run,daemon=True)
        self.thread.start()

    def run(self):
        buffer=bytearray()
        try:
            while True:
                data=self.socket.recv(4096)
                if not data: return
                buffer.extend(data)
                while len(buffer)>=8:
                    if buffer[:3] != b'\xff\x03\0': raise AssertionError('Unexpected request frame')
                    total=8+buffer[3]
                    if len(buffer)<total: break
                    request=bytes(buffer[:total]); del buffer[:total]
                    vendor=int.from_bytes(request[4:6],'big'); command=int.from_bytes(request[6:8],'big'); payload=request[8:]
                    self.requests.append((vendor,command,payload))
                    if self.stall: continue
                    response=self.answers.get(request)
                    if self.large_peers:
                        if command==0x1400: response=self.reply(vendor,command,b'\x00\x40')
                        elif command==0x1407: response=self.reply(vendor,command,b'\0')
                        elif command==0x1409: response=self.reply(vendor,command,b'\2')
                        elif command==0x1401: response=self.reply(vendor,command,bytes((payload[0],payload[0],0))+b'A'*128)
                    if command==0x1206 and self.model is not None:
                        response=self.reply(vendor,command,self.model)
                    elif command==0x1607:
                        response=self.reply(vendor,command,bytes([self.touch_locked]))
                    elif command==0x1606:
                        self.writes.append((command,payload)); self.touch_locked=bool(payload[0]); response=self.reply(vendor,command,b'')
                    if response is None:
                        response=self.reply(vendor,command,b'\x01',error=True)
                    for offset in range(0,len(response),3): self.socket.sendall(response[offset:offset+3])
        except (BrokenPipeError,ConnectionResetError,OSError): pass
        except BaseException as error: self.errors.append(error)

    @staticmethod
    def reply(vendor,command,payload,error=False):
        return bytes((255,3,0,len(payload)))+vendor.to_bytes(2,'big')+(command|(0x180 if error else 0x100)).to_bytes(2,'big')+payload

    def close(self):
        self.parent.close()
        self.socket.close()
        self.thread.join(1)
