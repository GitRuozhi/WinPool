import os
import argparse
import csv
import ctypes
import ctypes.wintypes as wintypes
import datetime
import gc
import io
import json
import re
import socket
import subprocess
import sys
import time
import winreg
import uuid
from collections import defaultdict
from contextlib import contextmanager
from typing import Any, Dict, List, Optional, Generator, Tuple

import psutil
import yaml

try:
    import py3nvml.py3nvml
    HAS_PY3NVML = True
except ImportError:
    HAS_PY3NVML = False

try:
    import wmi
    HAS_WMI = True
except ImportError:
    wmi = None
    HAS_WMI = False

import comtypes
from comtypes import IUnknown
import comtypes.client
from comtypes import CoInitialize, CoUninitialize

INVALID_STRS = {'', 'null', '$null', 'n/a', 'unknown', 'N/A'}
PRIORITY = ['adv_disk', 'adv_pool','adv_cim', 'adv_graph', 'cpucache', 'cim', 'reg', 'psutil', 'systeminfo', 'powershell', 'adv_vol']
EXCLUDE_ATTRS = {'CimClass', 'CimInstanceProperties', 'CimSystemProperties'}

DEFAULT_CONFIG = 'Full.yaml'
DEFAULT_TIMEOUT = 30
ADV_CIM_BATCH_SIZE = 32
OUTPUT_MAX_WIDTH = 128

def print_usage_guide():
    guide = f"""
{'='*60}
可用参数：
    -c, --config FILE   指定YAML配置文件，默认: Full.yaml
    -t, --timeout SEC   PowerShell超时时间，单位秒，默认30s
    --debug             调试模式，显示完整采集过程
    --no-export         禁止导出文件
    --web               以JSON格式输出，供前端调用
{'='*60}
"""
    print(guide)

def extract_number_from_str(text: Any) -> Any:
    if text is None or not isinstance(text, str):
        return text
    match = re.search(r'[\d,]+(?:\.\d+)?', text.replace(',', ''))
    if match:
        num_str = match.group().replace(',', '')
        try:
            return float(num_str) if '.' in num_str else int(num_str)
        except ValueError:
            return text
    return text

def is_valid(v) -> bool:
    if v is None:
        return False
    if isinstance(v, (list, tuple)):
        return True
    if isinstance(v, (str, bytes)):
        s = str(v).strip()
        return bool(s) and s.lower() not in INVALID_STRS
    if isinstance(v, dict):
        return bool(v)
    return True

def ps_json_load(text: str) -> Any:
    if not text or not text.strip():
        return None
    text = text.strip()
    if text.startswith('\ufeff'):
        text = text[1:].lstrip()
    if text in ('$null', 'null', 'None'):
        return None
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        if text.startswith('"') and text.endswith('"'):
            return text[1:-1]
        return text

def exec_batch(script_lines: List[str], timeout: int = 30) -> Tuple[bool, Any, str, Optional[str], int]:
    script = '\n'.join(script_lines)
    try:
        start = time.perf_counter()
        p = subprocess.run(
            ['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', script],
            capture_output=True, text=True, timeout=timeout
        )
        ms = int((time.perf_counter() - start) * 1000)
        out, err = p.stdout.strip(), p.stderr.strip()
        data = None
        json_error = None
        if out:
            try:
                data = json.loads(out)
            except json.JSONDecodeError as e:
                json_error = f"JSON解析失败: {str(e)[:50]}"
        return True, data, err, json_error, ms
    except subprocess.TimeoutExpired:
        return False, None, "超时", None, 0
    except Exception as e:
        return False, None, f"执行异常: {str(e)[:50]}", None, 0

def post_process(value: Any, steps: List[Dict]) -> Any:
    if not steps:
        return value
    cur = value
    for s in steps:
        t = s.get('type')
        if t == 'map':
            m = s.get('mapping', {})
            if isinstance(cur, list):
                cur = [m.get(str(x), str(x)) for x in cur]
            else:
                cur = m.get(str(cur), str(cur))
        elif t == 'calculate':
            op = s.get('operation', 'divide')
            by = s.get('by', 1.0)
            def _calc(v):
                if v is None:
                    return None
                try:
                    num = float(v) if not isinstance(v, (int, float)) else v
                except (ValueError, TypeError):
                    return str(v)
                if op == 'divide':
                    num = num / by
                elif op == 'multiply':
                    num = num * by
                return num
            if isinstance(cur, list):
                cur = [_calc(x) for x in cur]
            else:
                cur = _calc(cur)
        elif t == 'extract':
            idx = s.get('index', 0)
            pattern = s.get('pattern')
            if pattern:
                if isinstance(cur, list):
                    new_cur = []
                    for item in cur:
                        if isinstance(item, str):
                            match = re.search(pattern, item)
                            new_cur.append(match.group(1) if match else item)
                        else:
                            new_cur.append(item)
                    cur = new_cur
                elif isinstance(cur, str):
                    match = re.search(pattern, cur)
                    cur = match.group(1) if match else cur
            else:
                if isinstance(cur, list):
                    new_cur = []
                    for item in cur:
                        if isinstance(item, (list, tuple)) and len(item) > idx:
                            new_cur.append(item[idx])
                        elif isinstance(item, dict):
                            vals = list(item.values())
                            new_cur.append(vals[idx] if vals and len(vals) > idx else None)
                        else:
                            new_cur.append(item)
                    cur = new_cur
                elif isinstance(cur, dict):
                    vals = list(cur.values())
                    cur = vals[idx] if vals and len(vals) > idx else None
        elif t == 'trim_end':
            char = s.get('char', '\\')
            if isinstance(cur, str):
                cur = cur.rstrip(char)
            elif isinstance(cur, list):
                cur = [x.rstrip(char) if isinstance(x, str) else x for x in cur]
        elif t == 'extract_number':
            if isinstance(cur, (list, tuple)):
                cur = [extract_number_from_str(x) for x in cur]
            else:
                cur = extract_number_from_str(cur)
        elif t == 'uppercase_drive':
            def _upper_drive(p):
                if not isinstance(p, str):
                    return p
                return re.sub(r'^([a-zA-Z]):', lambda m: m.group(1).upper() + ':', p)
            if isinstance(cur, list):
                cur = [_upper_drive(x) for x in cur]
            else:
                cur = _upper_drive(cur)
        elif t == 'add_unit':
            unit = s.get('unit', '')
            def _add_unit(v):
                if v is None:
                    return ''
                if isinstance(v, (int, float)):
                    formatted = f"{v:g}" if v == int(v) else f"{v:.3f}".rstrip('0').rstrip('.')
                else:
                    formatted = str(v)
                return f"{formatted} {unit}" if unit else formatted
            if isinstance(cur, list):
                cur = [_add_unit(x) for x in cur]
            else:
                cur = _add_unit(cur)
        elif t == 'sum':
            if isinstance(cur, list):
                total = 0.0
                for item in cur:
                    try:
                        num = float(item)
                        total += num
                    except (ValueError, TypeError):
                        pass
                cur = int(total) if total.is_integer() else total
            else:
                cur = cur
                
        elif t == 'human_readable_size':
            unit = s.get('unit', 'Byte')
            units = ['Byte', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB']
            factors = [1, 1024, 1024**2, 1024**3, 1024**4, 1024**5]
            unit_to_factor = dict(zip(units, factors))

            def _convert(val):
                if val is None:
                    return None
                if isinstance(val, str):
                    return val
                if isinstance(val, (int, float)):
                    if val == 0:
                        return "0"   
                    factor = unit_to_factor.get(unit, 1)
                    bytes_val = val * factor
                    best_unit = None
                    best_val = None
                    for i, u in enumerate(reversed(units)):
                        f = factors[len(units)-1-i]
                        conv = bytes_val / f
                        if 16 <= conv <= 16384:
                            best_unit = u
                            best_val = conv
                            break
                    if best_unit is None:
                        candidates = [(bytes_val / f, u) for f, u in zip(factors, units)]
                        if bytes_val < 16 * unit_to_factor[units[0]]:
                            best_unit = units[0]
                            best_val = bytes_val / factors[0]
                        else:
                            best_unit = units[-1]
                            best_val = bytes_val / factors[-1]
                    return f"{best_val:g} {best_unit}"
                return val  

            if isinstance(cur, list):
                cur = [_convert(x) for x in cur]
            else:
                cur = _convert(cur)

    return cur

def resolve(ref, anchors):
    if isinstance(ref, list):
        return ref
    if isinstance(ref, str) and ref.startswith('*'):
        return anchors.get(ref[1:], [])
    return []

def parse_item(item: Dict, anchors: Dict) -> Dict:
    out = {
        'id': item['id'],
        'category': item['category'],
        'cn': item['chinese_name'],
        'std': item.get('standard_name', ''),
        'multiplicity': item.get('multiplicity', 0),
        'hide': item.get('hide', False), 
        'methods': {}
    }
    if cim := item.get('CIM'):
        out['methods']['cim'] = {
            'class': cim['ClassName'],
            'prop': cim['Property'],
            'namespace': cim.get('Namespace'),
            'post': resolve(cim.get('post_process', []), anchors)
        }
    if reg := item.get('REG'):
        out['methods']['reg'] = {
            'key': reg['key'],
            'val': reg['value'],
            'post': resolve(reg.get('post_process', []), anchors)
        }
    if ps := item.get('psutil'):
        out['methods']['psutil'] = {
            'impl': ps['impl'],
            'post': resolve(ps.get('post_process', []), anchors)
        }
    if ps_cfg := item.get('Powershell'):
        if not isinstance(ps_cfg, list):
            ps_cfg = [ps_cfg]
        cmds = []
        posts = []
        for c in ps_cfg:
            cmds.append(c['command'])
            posts.append(resolve(c.get('post_process', []), anchors))
        out['methods']['powershell'] = {
            'cmds': cmds,
            'posts': posts
        }
    if si := item.get('SystemInfo'):
        out['methods']['systeminfo'] = {
            'field': si['field'],
            'post': resolve(si.get('post_process', []), anchors)
        }
    if cpucache := item.get('cpucache'):
        out['methods']['cpucache'] = {
            'level': cpucache.get('level'),
            'post': resolve(cpucache.get('post_process', []), anchors)
        }
    if adv := item.get('adv_graph'):
        out['methods']['adv_graph'] = {
            'type': adv.get('type'),
            'field': adv.get('field'),
            'post': resolve(adv.get('post_process', []), anchors)
        }
    if adv_disk := item.get('adv_disk'):
        props = adv_disk.get('properties')
        if props is None:
            prop = adv_disk.get('property')
            props = [prop] if prop else []
        out['methods']['adv_disk'] = {
            'properties': props,
            'default': adv_disk.get('default'),
            'post': resolve(adv_disk.get('post_process', []), anchors)
        }
    if adv_cim := item.get('adv_cim'):
        out['methods']['adv_cim'] = {
            'sources': adv_cim.get('sources', []),
            'post': resolve(adv_cim.get('post_process', []), anchors)
        }
    if adv_vol := item.get('adv_vol'):
        out['methods']['adv_vol'] = {
            'properties': adv_vol.get('properties', []),
            'default': adv_vol.get('default'),
            'post': resolve(adv_vol.get('post_process', []), anchors)
        }
    if adv_pool := item.get('adv_pool'):
        out['methods']['adv_pool'] = {
            'type': adv_pool.get('type', 'both'),
            'properties': adv_pool.get('properties', []),
            'default': adv_pool.get('default'),
            'post': resolve(adv_pool.get('post_process', []), anchors)
        }
    return out

def find_config(path: str, default_name: str = DEFAULT_CONFIG) -> str:
    if os.path.exists(path):
        return path
    base_dir = os.path.dirname(os.path.abspath(__file__))
    candidate = os.path.join(base_dir, default_name)
    if os.path.exists(candidate):
        return candidate
    for entry in os.listdir(base_dir):
        sub = os.path.join(base_dir, entry)
        if os.path.isdir(sub):
            candidate = os.path.join(sub, default_name)
            if os.path.exists(candidate):
                return candidate
    raise FileNotFoundError(f"找不到配置文件: {path} 及当前目录/子目录下的 {default_name}")

def load_config(path: str) -> Tuple[Dict, List[Dict]]:
    path = find_config(path) 
    with open(path, 'r', encoding='utf-8') as f:
        cfg = yaml.safe_load(f)
    anchors = {k: v for k, v in cfg.items() if k != 'items' and isinstance(v, list)}
    raw_items = cfg['items']
    items = [parse_item(i, anchors) for i in raw_items]
    return anchors, items

def collect_cim(items: List[Dict], timeout: int) -> Dict[str, Dict]:
    target_items = [it for it in items if 'cim' in it['methods']]
    if not target_items:
        return {}

    script = ['$r=@{}; $e=@{}']
    for it in target_items:
        m = it['methods']['cim']
        var = f'v{it["id"].replace("-","_")}'
        cmd = f"Get-CimInstance -ClassName {m['class']}"
        if m.get('namespace'):
            cmd += f" -Namespace '{m['namespace']}'"
        cmd += f" | Select-Object -ExpandProperty {m['prop']}"
        script.extend([
            f'$oldEA = $ErrorActionPreference',
            f'$ErrorActionPreference = "Stop"',
            f'${var} = try {{ {cmd} }} catch {{ $e["{it["id"]}"] = $_.Exception.Message; $null }}',
            f'$ErrorActionPreference = $oldEA',
            f'$r["{it["id"]}"] = if(${var} -eq $null){{"null"}}else{{${var} | ConvertTo-Json -Compress}}'
        ])

    script.append('@{"results"=$r;"errors"=$e} | ConvertTo-Json -Compress')
    ok, data, script_err, json_err, ms = exec_batch(script, timeout)

    results = {}
    for it in target_items:
        rid = it['id']
        m = it['methods']['cim']
        r = {'raw': None, 'proc': None, 'ok': False, 'err': '', 'warn': ''}
        if not ok:
            r['err'] = f"脚本执行失败: {script_err}"
        elif json_err:
            r['warn'] = json_err
        else:
            raw = data.get('results', {}).get(rid)
            perr = data.get('errors', {}).get(rid)
            if perr:
                r['err'] = perr
            else:
                r['raw'] = ps_json_load(raw)
                r['proc'] = post_process(r['raw'], m['post'])
                r['ok'] = is_valid(r['proc'])
        results[rid] = r
    return results

def collect_ps(items: List[Dict], timeout: int) -> Dict[str, Dict]:
    target_items = [it for it in items if 'powershell' in it['methods']]
    if not target_items:
        return {}

    script = ['$results=@{}; $errors=@{}']
    for it in target_items:
        m = it['methods']['powershell']
        rid = it['id']
        for idx, cmd_text in enumerate(m['cmds']):
            var_val = f'ps_{rid.replace("-","_")}_{idx}_val'
            script.extend([
                f'$oldEA = $ErrorActionPreference',
                f'$ErrorActionPreference = "Stop"',
                f'${var_val} = try {{ & {{ {cmd_text} }} }} catch {{ $errors["{rid}_{idx}"] = $_.Exception.Message; $null }}',
                f'$ErrorActionPreference = $oldEA',
                f'if ($null -ne ${var_val}) {{',
                f'    $json = ${var_val} | ConvertTo-Json -Compress -Depth 10',
                f'    if (-not $results["{rid}"]) {{ $results["{rid}"] = @{{}} }}',
                f'    $results["{rid}"]["{idx}"] = $json',
                f'}}'
            ])
    script.append('@{"results"=$results;"errors"=$errors} | ConvertTo-Json -Compress -Depth 10')
    ok, data, script_err, json_err, ms = exec_batch(script, timeout)

    results = {}
    for it in target_items:
        rid = it['id']
        m = it['methods']['powershell']
        cmds = m['cmds']
        posts = m['posts']

        commands_info = []
        final_proc = None
        final_ok = False

        if not ok:
            err_msg = script_err or "脚本执行失败"
            if json_err:
                err_msg = json_err
            for i, cmd_text in enumerate(cmds):
                commands_info.append({
                    'index': i,
                    'status': 'FAILED',
                    'error': err_msg,
                    'raw': None,
                    'proc': None,
                    'ok': False
                })
        else:
            raw_dict = data.get('results', {}).get(rid, {})
            errors_dict = data.get('errors', {})
            for i, cmd_text in enumerate(cmds):
                cmd_info = {
                    'index': i,
                    'status': None,
                    'error': '',
                    'raw': None,
                    'proc': None,
                    'ok': False
                }
                err_key = f"{rid}_{i}"
                if err_key in errors_dict:
                    cmd_info['status'] = 'FAILED'
                    cmd_info['error'] = errors_dict[err_key]
                else:
                    raw_json = raw_dict.get(str(i))
                    if raw_json is None:
                        cmd_info['status'] = 'NO_DATA'
                    else:
                        raw = ps_json_load(raw_json)
                        proc = post_process(raw, posts[i])
                        valid = is_valid(proc)
                        cmd_info['status'] = 'SUCCESS' if valid else 'NO_DATA'
                        cmd_info['raw'] = raw
                        cmd_info['proc'] = proc
                        cmd_info['ok'] = valid
                commands_info.append(cmd_info)
            for cmd_info in commands_info:
                if cmd_info['status'] == 'SUCCESS':
                    final_proc = cmd_info['proc']
                    final_ok = True
                    break

        results[rid] = {
            'raw': None,
            'proc': final_proc,
            'ok': final_ok,
            'err': '',
            'warn': '',
            'commands': commands_info
        }
    return results

def collect_reg(items: List[Dict]) -> Dict[str, Dict]:
    results = {}
    for it in items:
        if 'reg' not in it['methods']:
            continue
        m = it['methods']['reg']
        rid = it['id']
        r = {'raw': None, 'proc': None, 'ok': False, 'err': '', 'warn': ''}
        try:
            key = m['key']
            if key.startswith('HKLM:\\'):
                hive, sub = winreg.HKEY_LOCAL_MACHINE, key[6:].lstrip('\\')
            elif key.startswith('HKCU:\\'):
                hive, sub = winreg.HKEY_CURRENT_USER, key[6:].lstrip('\\')
            else:
                raise ValueError(f'未知根键: {key}')
            access = winreg.KEY_READ | getattr(winreg, 'KEY_WOW64_64KEY', 0)
            with winreg.OpenKey(hive, sub, 0, access) as h:
                raw, _ = winreg.QueryValueEx(h, m['val'])
            r['raw'] = raw
            r['proc'] = post_process(raw, m['post'])
            r['ok'] = is_valid(r['proc'])
        except FileNotFoundError:
            r['err'] = '键或值不存在'
        except PermissionError:
            r['err'] = '权限不足'
        except Exception as e:
            r['err'] = str(e)[:100]
        results[rid] = r
    return results

def collect_psutil(items: List[Dict]) -> Dict[str, Dict]:
    results = {}
    for it in items:
        if 'psutil' not in it['methods']:
            continue
        m = it['methods']['psutil']
        rid = it['id']
        r = {'raw': None, 'proc': None, 'ok': False, 'err': '', 'warn': ''}
        try:
            impl = m['impl']
            if impl == 'virtual_memory.total':
                raw = psutil.virtual_memory().total
            elif impl == 'hostname':
                raw = socket.gethostname()
            elif impl == 'disk_partitions':
                raw = [p.device for p in psutil.disk_partitions(all=True)]
            elif impl == 'firmware_mode':
                k32 = ctypes.windll.kernel32
                buf = ctypes.create_unicode_buffer(1024)
                k32.GetFirmwareEnvironmentVariableW("", "{00000000-0000-0000-0000-000000000000}", buf, 1024)
                err = k32.GetLastError()
                raw = "Legacy" if err == 1 else "UEFI" if err == 1168 else f"Unknown({err})"
            elif impl == 'cpu_count.physical':
                raw = psutil.cpu_count(logical=False)
            elif impl == 'cpu_count.logical':
                raw = psutil.cpu_count(logical=True)
            else:
                raise ValueError(f'未知实现: {impl}')
            r['raw'] = raw
            r['proc'] = post_process(raw, m['post'])
            r['ok'] = is_valid(r['proc'])
        except Exception as e:
            r['err'] = str(e)[:100]
        results[rid] = r
    return results

def collect_systeminfo(items: List[Dict], timeout: int = 10) -> Dict[str, Dict]:
    target_items = [it for it in items if 'systeminfo' in it['methods']]
    if not target_items:
        return {}

    def run_systeminfo(use_csv: bool) -> Optional[str]:
        cmd = ['systeminfo']
        if use_csv:
            cmd.append('/FO')
            cmd.append('CSV')
        try:
            p = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                timeout=timeout,
                encoding='oem'
            )
            if p.returncode != 0:
                return None
            return p.stdout.strip()
        except:
            return None

    output = run_systeminfo(True)
    parsed = {}
    if output:
        try:
            reader = csv.reader(io.StringIO(output))
            rows = list(reader)
            if len(rows) >= 2:
                headers = [h.strip('"') for h in rows[0]]
                values = [v.strip('"') for v in rows[1]]
                parsed = dict(zip(headers, values))
        except Exception:
            parsed = {}

    if not parsed:
        output = run_systeminfo(False)
        if output:
            for line in output.splitlines():
                if re.match(r'^[^:]+:', line):
                    parts = line.split(':', 1)
                    key = parts[0].strip()
                    val = parts[1].strip() if len(parts) > 1 else ''
                    if key and val:
                        parsed[key] = val

    results = {}
    for it in target_items:
        rid = it['id']
        m = it['methods']['systeminfo']
        field = m['field']
        r = {'raw': None, 'proc': None, 'ok': False, 'err': '', 'warn': ''}
        raw_val = parsed.get(field)
        if raw_val is None:
            r['err'] = f'字段 "{field}" 未找到'
        else:
            r['raw'] = raw_val
            r['proc'] = post_process(raw_val, m['post'])
            r['ok'] = is_valid(r['proc'])
        results[rid] = r
    return results

class DISPLAY_DEVICEA(ctypes.Structure):
    _fields_ = [
        ('cb',           wintypes.DWORD),
        ('DeviceName',   wintypes.CHAR * 32),
        ('DeviceString', wintypes.CHAR * 128),
        ('StateFlags',   wintypes.DWORD),
        ('DeviceID',     wintypes.CHAR * 128),
        ('DeviceKey',    wintypes.CHAR * 128)
    ]

class DEVMODEA(ctypes.Structure):
    _fields_ = [
        ('dmDeviceName',     wintypes.CHAR * 32),
        ('dmSpecVersion',    wintypes.WORD),
        ('dmDriverVersion',  wintypes.WORD),
        ('dmSize',           wintypes.WORD),
        ('dmDriverExtra',    wintypes.WORD),
        ('dmFields',         wintypes.DWORD),
        ('dmPosition_x',     wintypes.LONG),
        ('dmPosition_y',     wintypes.LONG),
        ('dmDisplayOrientation', wintypes.DWORD),
        ('dmDisplayFixedOutput', wintypes.DWORD),
        ('dmColor',          wintypes.SHORT),
        ('dmDuplex',         wintypes.SHORT),
        ('dmYResolution',    wintypes.SHORT),
        ('dmTTOption',       wintypes.SHORT),
        ('dmCollate',        wintypes.SHORT),
        ('dmFormName',       wintypes.CHAR * 32),
        ('dmLogPixels',      wintypes.WORD),
        ('dmBitsPerPel',     wintypes.DWORD),
        ('dmPelsWidth',      wintypes.DWORD),
        ('dmPelsHeight',     wintypes.DWORD),
        ('dmDisplayFlags',   wintypes.DWORD),
        ('dmDisplayFrequency', wintypes.DWORD),
        ('dmICMMethod',      wintypes.DWORD),
        ('dmICMIntent',      wintypes.DWORD),
        ('dmMediaType',      wintypes.DWORD),
        ('dmDitherType',     wintypes.DWORD),
        ('dmReserved1',      wintypes.DWORD),
        ('dmReserved2',      wintypes.DWORD),
        ('dmPanningWidth',   wintypes.DWORD),
        ('dmPanningHeight',  wintypes.DWORD)
    ]

class DXGI_ADAPTER_DESC(ctypes.Structure):
    _fields_ = [
        ("Description", ctypes.c_wchar * 128),
        ("VendorId", ctypes.c_uint),
        ("DeviceId", ctypes.c_uint),
        ("SubSysId", ctypes.c_uint),
        ("Revision", ctypes.c_uint),
        ("DedicatedVideoMemory", ctypes.c_size_t),
        ("DedicatedSystemMemory", ctypes.c_size_t),
        ("SharedSystemMemory", ctypes.c_size_t),
        ("AdapterLuid", ctypes.c_int64),
    ]

class DXGI_OUTPUT_DESC(ctypes.Structure):
    _fields_ = [
        ("DeviceName", ctypes.c_wchar * 32),
        ("DesktopCoordinates", wintypes.RECT),
        ("AttachedToDesktop", wintypes.BOOL),
        ("Rotation", ctypes.c_uint),
        ("Monitor", wintypes.HANDLE),
    ]

class DXGI_OUTPUT_DESC1(ctypes.Structure):
    _fields_ = [
        ("DeviceName", ctypes.c_wchar * 32),
        ("DesktopCoordinates", wintypes.RECT),
        ("AttachedToDesktop", wintypes.BOOL),
        ("Rotation", ctypes.c_uint),
        ("Monitor", wintypes.HANDLE),
        ("BitsPerColor", ctypes.c_uint),
        ("ColorSpace", ctypes.c_uint),
        ("RedPrimary", ctypes.c_float * 2),
        ("GreenPrimary", ctypes.c_float * 2),
        ("BluePrimary", ctypes.c_float * 2),
        ("WhitePoint", ctypes.c_float * 2),
        ("MinLuminance", ctypes.c_float),
        ("MaxLuminance", ctypes.c_float),
        ("MaxFullFrameLuminance", ctypes.c_float),
    ]

IID_IDXGIFactory1 = comtypes.IID("{770aae78-f26f-4dba-a829-253c83d1b387}")
IID_IDXGIAdapter3 = comtypes.IID("{645967A4-1392-4310-A798-8053CE3E93FD}")
IID_ID3D12Device = comtypes.IID("{189819f1-1db6-4b57-be54-1821339b85f7}")
IID_IDXGIOutput6 = comtypes.IID("{068346e8-9ecf-42c6-a15d-eb0c5d9a6b8f}")

ENUM_CURRENT_SETTINGS = -1
user32 = ctypes.windll.user32
EnumDisplayDevices = user32.EnumDisplayDevicesA
EnumDisplaySettings = user32.EnumDisplaySettingsA

def edid_manufacturer_id(edid: bytes) -> str:
    if len(edid) < 10:
        return "未知"
    w = (edid[0x08] << 8) | edid[0x09]
    return ''.join(chr(((w >> (10 - i*5)) & 0x1F) + 64) for i in range(3))

def edid_product_code(edid: bytes) -> int:
    if len(edid) < 12:
        return 0
    return (edid[0x0A] << 8) | edid[0x0B]

def edid_monitor_name(edid: bytes) -> str:
    if len(edid) < 128:
        return ""
    for i in range(0x36, 0x7E, 18):
        if edid[i] == 0x00 and edid[i+1] == 0x00 and edid[i+3] == 0xFC:
            name = edid[i+5:i+18].decode('ascii', errors='ignore').strip('\x00\r\n ')
            return name
    return ""

def edid_bits_per_channel(edid: bytes) -> int:
    if len(edid) < 0x15:
        return 0
    if not (edid[0x14] & 0x80):
        return 0
    depth_code = (edid[0x14] >> 4) & 0x07
    depth_map = {0:0, 1:6, 2:8, 3:10, 4:12, 5:14, 6:16, 7:0}
    return depth_map.get(depth_code, 0)

def scan_edid_registry() -> Dict[str, Tuple[bytes, str, int, str]]:
    by_hardware_id = {}
    base = r"SYSTEM\CurrentControlSet\Enum\DISPLAY"
    try:
        key = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, base)
        i = 0
        while True:
            try:
                vendor_id = winreg.EnumKey(key, i)
                i += 1
                hardware_id = f"MONITOR\\{vendor_id}"
                vendor_key = winreg.OpenKey(key, vendor_id)
                j = 0
                while True:
                    try:
                        instance_id = winreg.EnumKey(vendor_key, j)
                        j += 1
                        params_path = f"{base}\\{vendor_id}\\{instance_id}\\Device Parameters"
                        try:
                            params_key = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, params_path)
                            edid, _ = winreg.QueryValueEx(params_key, "EDID")
                            winreg.CloseKey(params_key)
                            if len(edid) < 128:
                                continue
                            manu = edid_manufacturer_id(edid)
                            prod = edid_product_code(edid)
                            model = edid_monitor_name(edid)
                            if hardware_id not in by_hardware_id:
                                by_hardware_id[hardware_id] = (edid, manu, prod, model)
                        except FileNotFoundError:
                            pass
                    except OSError:
                        break
                winreg.CloseKey(vendor_key)
            except OSError:
                break
        winreg.CloseKey(key)
    except Exception:
        pass
    return by_hardware_id

@contextmanager
def dxgi_factory():
    CreateDXGIFactory1 = ctypes.windll.dxgi.CreateDXGIFactory1
    CreateDXGIFactory1.argtypes = [ctypes.POINTER(comtypes.IID), ctypes.POINTER(ctypes.POINTER(IUnknown))]
    CreateDXGIFactory1.restype = ctypes.c_long
    factory_ptr = ctypes.POINTER(IUnknown)()
    hr = CreateDXGIFactory1(ctypes.byref(IID_IDXGIFactory1), ctypes.byref(factory_ptr))
    if hr != 0 or not factory_ptr:
        yield None
        return
    try:
        yield factory_ptr
    finally:
        del factory_ptr

def get_adapter_desc(adapter_ptr) -> Optional[DXGI_ADAPTER_DESC]:
    vtbl = ctypes.cast(adapter_ptr, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    get_desc = ctypes.cast(
        vtbl[8],
        ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(DXGI_ADAPTER_DESC))
    )
    desc = DXGI_ADAPTER_DESC()
    if get_desc(adapter_ptr, ctypes.byref(desc)) == 0:
        return desc
    return None

def enum_adapters(keep_ref: bool = False) -> Generator[Tuple[DXGI_ADAPTER_DESC, ctypes.c_void_p], None, None]:
    with dxgi_factory() as factory:
        if not factory:
            return
        vtbl = ctypes.cast(factory, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
        enum_adapters = ctypes.cast(
            vtbl[7],
            ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_uint, ctypes.POINTER(ctypes.POINTER(IUnknown)))
        )
        i = 0
        while True:
            adapter_ptr = ctypes.POINTER(IUnknown)()
            hr = enum_adapters(factory, i, ctypes.byref(adapter_ptr))
            if hr != 0 or not adapter_ptr:
                break
            desc = get_adapter_desc(adapter_ptr)
            if desc:
                yield desc, adapter_ptr
                if not keep_ref:
                    del adapter_ptr
            else:
                del adapter_ptr
            i += 1

def enum_outputs(adapter_ptr, gpu_name: str) -> List[Dict[str, Any]]:
    outputs = []
    vtbl = ctypes.cast(adapter_ptr, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    enum_outputs = ctypes.cast(
        vtbl[7],
        ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_uint, ctypes.POINTER(ctypes.POINTER(IUnknown)))
    )
    i = 0
    while True:
        output_ptr = ctypes.POINTER(IUnknown)()
        hr = enum_outputs(adapter_ptr, i, ctypes.byref(output_ptr))
        if hr != 0 or not output_ptr:
            break

        vtbl_out = ctypes.cast(output_ptr, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
        get_desc = ctypes.cast(
            vtbl_out[7],
            ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(DXGI_OUTPUT_DESC))
        )
        desc = DXGI_OUTPUT_DESC()
        if get_desc(output_ptr, ctypes.byref(desc)) != 0:
            del output_ptr
            i += 1
            continue

        device_name = desc.DeviceName
        bits = 0
        hdr = False
        color_format = "Unkown"

        QueryInterface = ctypes.cast(
            vtbl_out[0],
            ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(comtypes.IID), ctypes.POINTER(ctypes.c_void_p))
        )
        output6_ptr = ctypes.POINTER(IUnknown)()
        hr_qi = QueryInterface(output_ptr, ctypes.byref(IID_IDXGIOutput6), ctypes.byref(output6_ptr))
        if hr_qi == 0 and output6_ptr:
            vtbl6 = ctypes.cast(output6_ptr, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
            GetDesc1 = ctypes.cast(
                vtbl6[24],
                ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(DXGI_OUTPUT_DESC1))
            )
            desc1 = DXGI_OUTPUT_DESC1()
            if GetDesc1(output6_ptr, ctypes.byref(desc1)) == 0:
                bits = desc1.BitsPerColor
                hdr = desc1.ColorSpace != 0
                cs = desc1.ColorSpace
                if 0 <= cs <= 11:
                    color_format = "RGB"
                elif 16 <= cs <= 27:
                    color_format = "YCbCr"
            del output6_ptr

        outputs.append({
            'device_name': device_name,
            'gpu_name': gpu_name,
            'bits_per_color': bits,
            'hdr': hdr,
            'color_format': color_format,
        })
        del output_ptr
        i += 1
    return outputs

def get_nvidia_gpu_detail() -> List[Dict[str, Any]]:
    if not HAS_PY3NVML:
        return []
    try:
        py3nvml.py3nvml.nvmlInit()
    except Exception:
        return []
    devices = []
    try:
        count = py3nvml.py3nvml.nvmlDeviceGetCount()
        for i in range(count):
            handle = py3nvml.py3nvml.nvmlDeviceGetHandleByIndex(i)
            name = py3nvml.py3nvml.nvmlDeviceGetName(handle)
            if isinstance(name, bytes):
                name = name.decode()
            driver = py3nvml.py3nvml.nvmlSystemGetDriverVersion()
            if isinstance(driver, bytes):
                driver = driver.decode()
            mem = py3nvml.py3nvml.nvmlDeviceGetMemoryInfo(handle)
            pci = py3nvml.py3nvml.nvmlDeviceGetPciInfo(handle)
            bus_id = pci.busId.decode() if isinstance(pci.busId, bytes) else pci.busId

            bus_hex = device_hex = function_hex = "00"
            try:
                parts = bus_id.split(':')
                if len(parts) == 3:
                    bus_hex = parts[1].zfill(2)
                    dev_func = parts[2].split('.')
                    device_hex = dev_func[0].zfill(2)
                    function_hex = dev_func[1].zfill(2) if len(dev_func) > 1 else "00"
            except Exception:
                pass

            vendor_id = device_id = 0
            try:
                pci_device_id = getattr(pci, 'pciDeviceId', 0)
                if pci_device_id:
                    vendor_id = pci_device_id & 0xFFFF
                    device_id = (pci_device_id >> 16) & 0xFFFF
            except Exception:
                pass

            dedicated_gib = mem.total / (1024 ** 3)
            devices.append({
                'name': name,
                'driver_version': driver,
                'dedicated_memory_gib': dedicated_gib,
                'used_memory_gib': mem.used / (1024 ** 3),
                'free_memory_gib': mem.free / (1024 ** 3),
                'bus_hex': bus_hex,
                'device_hex': device_hex,
                'function_hex': function_hex,
                'vendor_id': vendor_id,
                'device_id': device_id,
                'total_memory_gib': dedicated_gib,
            })
    except Exception:
        return []
    finally:
        try:
            py3nvml.py3nvml.nvmlShutdown()
        except Exception:
            pass
    return devices

def get_driver_info_from_wmi() -> Dict[Tuple[int, int], str]:
    if not HAS_WMI:
        return {}
    mapping = {}
    locator = None
    services = None
    controllers = None
    try:
        locator = comtypes.client.CreateObject("WbemScripting.SWbemLocator")
        services = locator.ConnectServer(".", "root\\cimv2")
        controllers = services.ExecQuery("SELECT * FROM Win32_VideoController")
        for ctrl in controllers:
            try:
                pnp = ctrl.PNPDeviceID
                if not pnp or "VEN_" not in pnp:
                    continue
                parts = pnp.split('\\')[1].split('&')
                ven = [p for p in parts if p.startswith("VEN_")][0][4:]
                dev = [p for p in parts if p.startswith("DEV_")][0][4:]
                vendor_id = int(ven, 16)
                device_id = int(dev, 16)
                mapping[(vendor_id, device_id)] = ctrl.DriverVersion
            except (AttributeError, IndexError, ValueError):
                continue
    except Exception:
        pass
    finally:
        if controllers:
            del controllers
        if services:
            del services
        if locator:
            del locator
    return mapping

def get_pci_location_from_registry(vendor_id: int, device_id: int) -> Tuple[str, str, str]:
    try:
        base_key = r"SYSTEM\CurrentControlSet\Enum\PCI"
        ven_dev = f"VEN_{vendor_id:04X}&DEV_{device_id:04X}"
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, base_key) as pci_key:
            i = 0
            while True:
                try:
                    subkey_name = winreg.EnumKey(pci_key, i)
                    if subkey_name.startswith(ven_dev):
                        with winreg.OpenKey(pci_key, subkey_name) as ven_key:
                            j = 0
                            while True:
                                try:
                                    instance_name = winreg.EnumKey(ven_key, j)
                                    instance_path = f"{subkey_name}\\{instance_name}"
                                    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, f"{base_key}\\{instance_path}") as inst_key:
                                        loc_info, _ = winreg.QueryValueEx(inst_key, "LocationInformation")
                                        if isinstance(loc_info, str):
                                            m = re.search(r";\((\d+),(\d+),(\d+)\)", loc_info)
                                            if m:
                                                bus, dev, func = map(int, m.groups())
                                                return f"{bus:02X}", f"{dev:02X}", f"{func:02X}"
                                            m = re.search(r"bus (\d+), device (\d+), function (\d+)", loc_info, re.I)
                                            if m:
                                                bus, dev, func = map(int, m.groups())
                                                return f"{bus:02X}", f"{dev:02X}", f"{func:02X}"
                                except OSError:
                                    break
                                j += 1
                    i += 1
                except OSError:
                    break
    except Exception:
        pass
    return "N/A", "N/A", "N/A"

def get_driver_version_from_registry(vendor_id: int, device_id: int) -> str:
    class_guid = "{4d36e968-e325-11ce-bfc1-08002be10318}"
    base_key = rf"SYSTEM\CurrentControlSet\Control\Class\{class_guid}"
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, base_key) as key:
            i = 0
            while True:
                try:
                    subkey_name = winreg.EnumKey(key, i)
                    i += 1
                    subkey_path = f"{base_key}\\{subkey_name}"
                    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, subkey_path) as subkey:
                        try:
                            driver_version, _ = winreg.QueryValueEx(subkey, "DriverVersion")
                        except FileNotFoundError:
                            continue
                        try:
                            matching_id, _ = winreg.QueryValueEx(subkey, "MatchingDeviceId")
                            if isinstance(matching_id, str):
                                parts = matching_id.split('\\')
                                if len(parts) > 1:
                                    ids = parts[1].split('&')
                                    ven = dev = None
                                    for item in ids:
                                        if item.startswith("VEN_"):
                                            ven = int(item[4:], 16)
                                        elif item.startswith("DEV_"):
                                            dev = int(item[4:], 16)
                                    if ven == vendor_id and dev == device_id:
                                        return driver_version
                        except FileNotFoundError:
                            pass
                        try:
                            ven_reg, _ = winreg.QueryValueEx(subkey, "HardwareInformation.VendorIdentifier")
                            dev_reg, _ = winreg.QueryValueEx(subkey, "HardwareInformation.DeviceIdentifier")
                            if ven_reg == vendor_id and dev_reg == device_id:
                                return driver_version
                        except FileNotFoundError:
                            pass
                except OSError:
                    break
    except Exception:
        pass
    return "未知"

def get_universal_gpu_info(desc: DXGI_ADAPTER_DESC,
                           driver_mapping: Dict[Tuple[int, int], str]) -> Dict[str, Any]:
    driver_version = driver_mapping.get((desc.VendorId, desc.DeviceId), "未知")
    if driver_version == "未知":
        driver_version = get_driver_version_from_registry(desc.VendorId, desc.DeviceId)
    bus, dev, func = get_pci_location_from_registry(desc.VendorId, desc.DeviceId)
    dedicated_gib = desc.DedicatedVideoMemory / (1024**3)
    shared_gib = desc.SharedSystemMemory / (1024**3)
    return {
        'name': desc.Description.strip(),
        'driver_version': driver_version,
        'dedicated_memory_gib': dedicated_gib,
        'shared_memory_gib': shared_gib,
        'total_memory_gib': dedicated_gib + shared_gib,
        'bus_hex': bus,
        'device_hex': dev,
        'function_hex': func,
        'vendor_id': desc.VendorId,
        'device_id': desc.DeviceId,
    }

def get_d3d12_feature_level(adapter_name: str, adapter_ptr) -> str:
    FEATURE_LEVELS = [0xc200, 0xc100, 0xc000, 0xb100, 0xb000]
    try:
        d3d12 = ctypes.WinDLL("d3d12")
        D3D12CreateDevice = d3d12.D3D12CreateDevice
        D3D12CreateDevice.argtypes = [
            ctypes.c_void_p,
            ctypes.c_int,
            ctypes.POINTER(comtypes.IID),
            ctypes.POINTER(ctypes.c_void_p)
        ]
        D3D12CreateDevice.restype = ctypes.c_long
        for fl in FEATURE_LEVELS:
            device_ptr = ctypes.c_void_p()
            hr = D3D12CreateDevice(adapter_ptr, fl, ctypes.byref(IID_ID3D12Device), ctypes.byref(device_ptr))
            if hr == 0 and device_ptr:
                device_com = comtypes.cast(device_ptr, comtypes.POINTER(IUnknown))
                del device_com
                val = fl >> 8
                major = val // 16
                minor = val % 16
                return f"{major} (FL {major}.{minor})"
        return "不支持 DirectX 11 及以上"
    except Exception as e:
        return f"D3D12 检测失败: {e}"

def collect_gpu_and_output_info(driver_map: Dict[Tuple[int, int], str],
                                nvidia_gpus: List[Dict[str, Any]]
                                ) -> Tuple[List[Dict[str, Any]], Dict[str, Dict[str, Any]]]:
    gpu_infos = []
    output_info_map = {}
    adapters_to_release = []

    try:
        for desc, adapter_ptr in enum_adapters(keep_ref=True):
            adapters_to_release.append(adapter_ptr)
            vendor = desc.VendorId
            device = desc.DeviceId

            if vendor == 0x10DE and (vendor, device) in {(g['vendor_id'], g['device_id']) for g in nvidia_gpus}:
                nv_gpu = next(g for g in nvidia_gpus if g['vendor_id'] == vendor and g['device_id'] == device)
                dedicated = nv_gpu['dedicated_memory_gib']
                shared = desc.SharedSystemMemory / (1024**3)
                info = {
                    'name': nv_gpu['name'],
                    'driver_version': nv_gpu['driver_version'],
                    'dedicated_memory_gib': dedicated,
                    'shared_memory_gib': shared,
                    'total_memory_gib': dedicated + shared,
                    'bus_hex': nv_gpu['bus_hex'],
                    'device_hex': nv_gpu['device_hex'],
                    'function_hex': nv_gpu['function_hex'],
                    'vendor_id': vendor,
                    'device_id': device,
                }
            else:
                info = get_universal_gpu_info(desc, driver_map)
                if vendor == 0x1414:
                    info['driver_version'] = "Software"

            info['dx_version'] = get_d3d12_feature_level(desc.Description, adapter_ptr)
            gpu_infos.append(info)

            outputs = enum_outputs(adapter_ptr, info['name'])
            for out in outputs:
                output_info_map[out['device_name']] = out
    finally:
        adapters_to_release.clear()
        del adapters_to_release

    return gpu_infos, output_info_map

def enumerate_monitors(edid_map, output_info_map):
    monitors = []
    adapter_idx = 0
    screen_num = 0
    while True:
        adapter = DISPLAY_DEVICEA()
        adapter.cb = ctypes.sizeof(DISPLAY_DEVICEA)
        if not EnumDisplayDevices(None, adapter_idx, ctypes.byref(adapter), 0):
            break
        if adapter.StateFlags & 0x00000001:
            monitor_idx = 0
            while True:
                monitor = DISPLAY_DEVICEA()
                monitor.cb = ctypes.sizeof(DISPLAY_DEVICEA)
                if not EnumDisplayDevices(adapter.DeviceName, monitor_idx, ctypes.byref(monitor), 0):
                    break
                if monitor.StateFlags & 0x00000001:
                    screen_num += 1
                    devmode = DEVMODEA()
                    devmode.dmSize = ctypes.sizeof(DEVMODEA)
                    if EnumDisplaySettings(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ctypes.byref(devmode)):
                        width = devmode.dmPelsWidth
                        height = devmode.dmPelsHeight
                        refresh = devmode.dmDisplayFrequency
                        bpp = devmode.dmBitsPerPel
                        pos_x = devmode.dmPosition_x
                        pos_y = devmode.dmPosition_y
                    else:
                        width = height = refresh = bpp = pos_x = pos_y = 0

                    gpu_name = adapter.DeviceString.decode('utf-8', errors='ignore').strip('\x00')
                    device_id = monitor.DeviceID.decode('utf-8', errors='ignore').strip('\x00')
                    hardware_id = '\\'.join(device_id.split('\\')[:2])
                    output_name = monitor.DeviceName.decode('utf-8', errors='ignore').strip('\x00')

                    base_match = re.search(r'(\\\\\.\\\\DISPLAY\d+)', output_name)
                    base_output_name = base_match.group(1) if base_match else output_name
                    display_match = re.search(r'(DISPLAY\d+)', output_name)
                    display_id = display_match.group(1) if display_match else ""

                    model = "未知"
                    manufacturer = "未知"
                    product_code = ""
                    edid_bpc = 0
                    if hardware_id in edid_map:
                        edid, manu, prod, mname = edid_map[hardware_id]
                        manufacturer = manu
                        model = mname if mname else "未知型号"
                        product_code = f"{prod:04X}" if prod else ""
                        edid_bpc = edid_bits_per_channel(edid)

                    monitors.append({
                        'screen_num': screen_num,
                        'base_output_name': base_output_name,
                        'display_id': display_id,
                        'gpu_name': gpu_name,
                        'model': model,
                        'manufacturer': manufacturer,
                        'product_code': product_code,
                        'edid_bpc': edid_bpc,
                        'width': width,
                        'height': height,
                        'refresh': refresh,
                        'bpp': bpp,
                        'pos_x': pos_x,
                        'pos_y': pos_y,
                        'is_primary': (pos_x == 0 and pos_y == 0),
                    })
                monitor_idx += 1
        adapter_idx += 1
    return monitors

def collect_monitor_info(edid_map, output_info_map):
    monitors = enumerate_monitors(edid_map, output_info_map)
    for mon in monitors:
        out_info = output_info_map.get(mon['base_output_name'], {})
        if out_info:
            mon['gpu_name'] = out_info.get('gpu_name', mon['gpu_name'])
            hdr = out_info.get('hdr', False)
            color_format = out_info.get('color_format', 'Unkown')
        else:
            hdr = False
            color_format = 'Unkown'

        final_bits = mon['edid_bpc'] if mon['edid_bpc'] > 0 else 0
        mon['bits_per_color'] = final_bits
        mon['hdr'] = hdr

        if final_bits > 0:
            mon['depth_str'] = f"{final_bits} bpc"
        else:
            bpp = mon['bpp']
            if bpp == 32:
                mon['depth_str'] = "8 bpc 推测"
            elif bpp == 30:
                mon['depth_str'] = "10 bpc 推测"
            elif bpp == 64:
                mon['depth_str'] = "16 bpc 推测"
            else:
                mon['depth_str'] = "未知"

        mon['color_format'] = color_format
        mon['dynamic_range'] = "HDR" if hdr else "SDR"
    return monitors

def collect_adv_graph(items: List[Dict]) -> Tuple[List[Dict], List[Dict]]:
    edid_map = scan_edid_registry()
    nvidia_gpus = get_nvidia_gpu_detail()
    driver_map = get_driver_info_from_wmi()
    gpu_list, output_info_map = collect_gpu_and_output_info(driver_map, nvidia_gpus)
    monitor_list = collect_monitor_info(edid_map, output_info_map)
    return gpu_list, monitor_list

def process_adv_graph_items(items: List[Dict], gpu_list: List[Dict], monitor_list: List[Dict]) -> Dict[str, Dict]:
    results = {}
    for it in items:
        if 'adv_graph' not in it['methods']:
            continue
        m = it['methods']['adv_graph']
        typ = m.get('type')
        field = m.get('field')
        data_list = gpu_list if typ == 'gpu' else monitor_list if typ == 'display' else []
        values = [obj.get(field) for obj in data_list if field in obj]
        proc = post_process(values, m.get('post', []))
        rid = it['id']
        results[rid] = {
            'raw': data_list,
            'proc': proc,
            'ok': bool(values),
            'err': '' if values else '无数据',
            'warn': ''
        }
    return results

class CACHE_DESCRIPTOR(ctypes.Structure):
    _fields_ = [
        ("Level", ctypes.c_byte),
        ("Associativity", ctypes.c_byte),
        ("LineSize", ctypes.c_ushort),
        ("Size", ctypes.c_uint),
        ("Type", ctypes.c_uint),
    ]

class _UNION(ctypes.Union):
    _fields_ = [
        ("Cache", CACHE_DESCRIPTOR),
        ("Reserved", ctypes.c_ulonglong * 2),
    ]

class SYSTEM_LOGICAL_PROCESSOR_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("ProcessorMask", ctypes.c_size_t),
        ("Relationship", ctypes.c_uint),
        ("u", _UNION),
    ]

def get_cache_info() -> Tuple[List[int], List[int], List[int]]:
    kernel32 = ctypes.windll.kernel32
    GetLogicalProcessorInformation = kernel32.GetLogicalProcessorInformation
    GetLogicalProcessorInformation.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
    GetLogicalProcessorInformation.restype = ctypes.c_bool

    buffer_size = ctypes.c_ulong(0)
    ret = GetLogicalProcessorInformation(None, ctypes.byref(buffer_size))
    if not ret and ctypes.GetLastError() != 122:
        return [], [], []

    buffer = (ctypes.c_byte * buffer_size.value)()
    if not GetLogicalProcessorInformation(ctypes.cast(buffer, ctypes.c_void_p), ctypes.byref(buffer_size)):
        return [], [], []

    entries = []
    offset = 0
    entry_size = ctypes.sizeof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION)
    while offset < buffer_size.value:
        entry = SYSTEM_LOGICAL_PROCESSOR_INFORMATION.from_buffer_copy(buffer, offset)
        entries.append(entry)
        offset += entry_size

    l1, l2, l3 = [], [], []
    for e in entries:
        if e.Relationship == 2:
            cache = e.u.Cache
            size_kb = cache.Size // 1024
            if cache.Level == 1:
                l1.append(size_kb)
            elif cache.Level == 2:
                l2.append(size_kb)
            elif cache.Level == 3:
                l3.append(size_kb)
    return l1, l2, l3

def collect_cpucache(items: List[Dict]) -> Dict[str, Dict]:
    l1, l2, l3 = get_cache_info()
    results = {}
    for it in items:
        if 'cpucache' not in it['methods']:
            continue
        m = it['methods']['cpucache']
        level = m.get('level', '').upper()
        if level == 'L1':
            raw = l1
        elif level == 'L2':
            raw = l2
        elif level == 'L3':
            raw = l3
        else:
            raw = []
        proc = post_process(raw, m.get('post', []))
        rid = it['id']
        results[rid] = {
            'raw': raw,
            'proc': proc,
            'ok': bool(raw),
            'err': '' if raw else '无缓存数据',
            'warn': ''
        }
    return results

def parse_physical_location(loc: Optional[str]) -> Dict[str, str]:
    result = {
        'Prefix': '',
        'Bus': '',
        'Device': '',
        'Function': '',
        'Adapter': '',
        'Port': '',
        'Target': '',
        'LUN': ''
    }
    if not loc or not isinstance(loc, str):
        return result

    if ':' in loc:
        prefix = loc.split(':', 1)[0].strip()
        result['Prefix'] = prefix

    parts = [p.strip() for p in loc.replace(':', ' ').split() if p.strip()]
    i = 0
    while i < len(parts):
        if parts[i] in ('Bus', 'Device', 'Function', 'Adapter', 'Port', 'Target', 'LUN') and i+1 < len(parts) and parts[i+1].isdigit():
            result[parts[i]] = parts[i+1]
            i += 2
        else:
            i += 1
    return result

def collect_adv_cim(items: List[Dict], timeout: int) -> Dict[str, Dict]:
    target_items = [it for it in items if 'adv_cim' in it['methods']]
    if not target_items:
        return {}

    sub_tasks = []
    for it in target_items:
        sources = it['methods']['adv_cim']['sources']
        for idx, src in enumerate(sources):
            sub_tasks.append((it['id'], idx, src))

    all_data = {'results': {}, 'errors': {}}
    batch_num = 0
    total_ok = True
    global_script_err = ''
    global_json_err = ''

    for i in range(0, len(sub_tasks), ADV_CIM_BATCH_SIZE):
        batch = sub_tasks[i:i+ADV_CIM_BATCH_SIZE]
        batch_num += 1
        script = ['$r=@{}; $e=@{}']
        for (item_id, sub_idx, src) in batch:
            var_name = f"v{item_id.replace('-','_')}_{sub_idx}"
            cmd = f"Get-CimInstance -ClassName {src['ClassName']}"
            if src.get('Namespace'):
                cmd += f" -Namespace '{src['Namespace']}'"
            cmd += f" | Select-Object -ExpandProperty {src['Property']}"
            script.extend([
                f'$oldEA = $ErrorActionPreference',
                f'$ErrorActionPreference = "Stop"',
                f'${var_name} = try {{ {cmd} }} catch {{ $e["{item_id}_{sub_idx}"] = $_.Exception.Message; $null }}',
                f'$ErrorActionPreference = $oldEA',
                f'$r["{item_id}_{sub_idx}"] = if(${var_name} -eq $null){{"null"}}else{{${var_name} | ConvertTo-Json -Compress}}'
            ])
        script.append('@{"results"=$r;"errors"=$e} | ConvertTo-Json -Compress')
        ok, data, script_err, json_err, ms = exec_batch(script, timeout)
        if not ok:
            total_ok = False
            global_script_err = script_err
        if json_err:
            global_json_err = json_err
        if data:
            for key, val in data.get('results', {}).items():
                all_data['results'][key] = val
            for key, val in data.get('errors', {}).items():
                all_data['errors'][key] = val

    results = {}
    success_count = 0
    for it in target_items:
        rid = it['id']
        sources = it['methods']['adv_cim']['sources']
        overall_post = it['methods']['adv_cim'].get('post', [])

        sub_values = []
        errors = []

        for idx, src in enumerate(sources):
            task_key = f"{rid}_{idx}"
            mode = src.get('mode', 'contact')
            raw_val = None
            err_msg = ''
            if not total_ok:
                err_msg = f"脚本执行失败: {global_script_err}"
            elif global_json_err:
                pass
            else:
                raw = all_data.get('results', {}).get(task_key)
                perr = all_data.get('errors', {}).get(task_key)
                if perr:
                    err_msg = perr
                else:
                    raw_val = ps_json_load(raw)
                    post_steps = src.get('post_process', [])
                    if post_steps:
                        try:
                            raw_val = post_process(raw_val, post_steps)
                        except Exception as e:
                            err_msg = f"后处理异常: {str(e)[:100]}"
            if err_msg:
                errors.append(f"子任务{idx}({src.get('ClassName')}.{src.get('Property')}): {err_msg}")
                lst = []
            else:
                lst = raw_val if isinstance(raw_val, list) else [raw_val] if raw_val is not None else []
            if mode == 'emp_contact':
                lst = [None] * len(lst)
            sub_values.append(lst)

        combined = [item for subl in sub_values for item in subl]
        if overall_post:
            try:
                combined = post_process(combined, overall_post)
            except Exception as e:
                errors.append(f"整体后处理异常: {str(e)[:100]}")

        ok_flag = is_valid(combined)
        if ok_flag:
            success_count += 1
        results[rid] = {
            'raw': combined,
            'proc': combined,
            'ok': ok_flag,
            'err': '; '.join(errors) if errors else '',
            'warn': ''
        }
    return results
def collect_adv_disk(items: List[Dict], timeout: int) -> Dict[str, Dict]:
    """
    采集高级磁盘信息（物理磁盘、虚拟磁盘、存储空间）。
    修复：只返回配置项中需要的属性，避免序列化问题；添加 InterfaceType 和 FirmwareRevision 支持。
    """
    target_items = [it for it in items if 'adv_disk' in it['methods']]
    if not target_items:
        return {}

    # 收集所有配置项中需要返回的属性（包括可能的后处理所需属性）
    all_props = set()
    for it in target_items:
        cfg = it['methods']['adv_disk']
        props = cfg.get('properties', [])
        if not props and 'property' in cfg:
            props = [cfg['property']]
        for p in props:
            if p:
                all_props.add(p)
    # 必需的辅助属性（用于唯一标识、关联和物理位置解析）
    required_props = {'ObjectId', 'UniqueId', 'DeviceId', 'Number', 'PhysicalLocation'}
    all_props.update(required_props)
    # 将集合转为列表，并准备 PowerShell 数组字符串
    prop_list = list(all_props)
    prop_list_str = ', '.join(f"'{p}'" for p in prop_list)

    # 构建 PowerShell 脚本
    script_template = r'''
$props = @(PROP_LIST)   # 需要返回的属性列表

# 过滤函数：只保留 $props 中存在的属性
function Get-FilteredObject($instance) {
    $hash = @{}
    $props | ForEach-Object {
        $propName = $_
        # 检查属性是否存在且不为 null
        if ($null -ne $instance.$propName) {
            $hash[$propName] = $instance.$propName
        }
    }
    [PSCustomObject]$hash
}

# 存储池映射 (UniqueId -> FriendlyName)
$pools = Get-CimInstance -Namespace root/microsoft/windows/storage -ClassName MSFT_StoragePool -ErrorAction SilentlyContinue
$poolUniqueToName = @{}
foreach ($pool in $pools) {
    if ($pool.UniqueId) {
        $poolUniqueToName[$pool.UniqueId.ToString()] = $pool.FriendlyName
    }
}

# 物理磁盘到池 UniqueId 的映射 (PhysicalDisk.ObjectId -> [StoragePool.UniqueId])
$physToPoolUniqueIds = @{}
$poolToDisk = Get-CimInstance -Namespace root/microsoft/windows/storage -ClassName MSFT_StoragePoolToPhysicalDisk -ErrorAction SilentlyContinue
foreach ($rel in $poolToDisk) {
    $pool = Get-CimInstance -InputObject $rel.StoragePool -ErrorAction SilentlyContinue
    $disk = Get-CimInstance -InputObject $rel.PhysicalDisk -ErrorAction SilentlyContinue
    if ($pool -and $disk -and $pool.UniqueId) {
        $physId = $disk.ObjectId.ToString()
        if (-not $physToPoolUniqueIds.ContainsKey($physId)) {
            $physToPoolUniqueIds[$physId] = @()
        }
        $physToPoolUniqueIds[$physId] += $pool.UniqueId.ToString()
    }
}

# 虚拟磁盘到池 UniqueId 的映射 (VirtualDisk.ObjectId -> [StoragePool.UniqueId])
$virtToPoolUniqueIds = @{}
$virtRaw = Get-CimInstance -Namespace root/microsoft/windows/storage -ClassName MSFT_VirtualDisk -ErrorAction SilentlyContinue
foreach ($v in $virtRaw) {
    $pool = Get-CimAssociatedInstance -InputObject $v -ResultClass MSFT_StoragePool -ErrorAction SilentlyContinue
    if ($pool -and $pool.UniqueId) {
        $virtToPoolUniqueIds[$v.ObjectId.ToString()] = @($pool.UniqueId.ToString())
    }
}

# 获取原始 MSFT_Disk 和 MSFT_PhysicalDisk 实例
$disksRaw = Get-CimInstance -Namespace root/microsoft/windows/storage -ClassName MSFT_Disk -ErrorAction SilentlyContinue
$physRaw = Get-CimInstance -Namespace root/microsoft/windows/storage -ClassName MSFT_PhysicalDisk -ErrorAction SilentlyContinue

# 获取 Win32_DiskDrive 的 InterfaceType 和 FirmwareRevision 映射
$win32Disks = Get-CimInstance Win32_DiskDrive -ErrorAction SilentlyContinue
$interfaceMap = @{}
$firmwareMap = @{}
foreach ($disk in $win32Disks) {
    $idx = $disk.Index
    if ($null -ne $idx) {
        $interfaceMap[$idx.ToString()] = $disk.InterfaceType
        $firmwareMap[$idx.ToString()] = $disk.FirmwareRevision
    }
}

# Disk -> VirtualDisk 关联
$diskToVirtMap = @{}
foreach ($disk in $disksRaw) {
    $vdisk = Get-CimAssociatedInstance -InputObject $disk -ResultClass MSFT_VirtualDisk -ErrorAction SilentlyContinue
    if ($vdisk) {
        $diskToVirtMap[$disk.ObjectId.ToString()] = $vdisk.ObjectId.ToString()
    }
}

# 生成过滤后的对象列表
$disks = $disksRaw | ForEach-Object { Get-FilteredObject $_ }
$phys = $physRaw | ForEach-Object { Get-FilteredObject $_ }
$virt = $virtRaw | ForEach-Object { Get-FilteredObject $_ }

# 返回所有数据
@{
    disks = $disks
    phys = $phys
    virt = $virt
    diskToVirt = $diskToVirtMap
    physToPoolUniqueIds = $physToPoolUniqueIds
    virtToPoolUniqueIds = $virtToPoolUniqueIds
    poolUniqueToName = $poolUniqueToName
    interfaceMap = $interfaceMap
    firmwareMap = $firmwareMap
} | ConvertTo-Json -Depth 3 -Compress
'''
    script = script_template.replace('PROP_LIST', prop_list_str)

    # 执行 PowerShell 脚本
    ok, data, script_err, json_err, ms = exec_batch([script], timeout)
    if not ok:
        print(f"  PowerShell执行失败: {script_err}")
        return {}
    if json_err:
        print(f"  JSON解析警告: {json_err}")

    if not isinstance(data, dict):
        print("  返回数据格式错误，期望字典")
        return {}

    # 辅助函数：确保列表中的元素是字典
    def ensure_dict_list(lst):
        if not isinstance(lst, list):
            return []
        return [item for item in lst if isinstance(item, dict)]

    disks = ensure_dict_list(data.get('disks', []))
    phys = ensure_dict_list(data.get('phys', []))
    virt = ensure_dict_list(data.get('virt', []))

    disk_to_virt = data.get('diskToVirt') or {}
    phys_to_pool_uids = data.get('physToPoolUniqueIds') or {}
    virt_to_pool_uids = data.get('virtToPoolUniqueIds') or {}
    pool_uid_to_name = data.get('poolUniqueToName') or {}
    interface_map = data.get('interfaceMap') or {}
    firmware_map = data.get('firmwareMap') or {}

    # 获取唯一键的函数
    def get_unique_key(obj):
        if not isinstance(obj, dict):
            return str(obj)
        if obj.get('UniqueId'):
            return str(obj['UniqueId']).strip()
        if obj.get('SerialNumber') and obj.get('Model'):
            return f"{str(obj['SerialNumber']).strip()}-{str(obj['Model']).strip()}"
        if obj.get('DeviceId'):
            return str(obj['DeviceId']).strip()
        if obj.get('ObjectId'):
            match = re.search(r'{([0-9A-Fa-f-]+)}', obj['ObjectId'])
            if match:
                return match.group(1)
            return str(obj['ObjectId'])
        import uuid
        return str(uuid.uuid4())

    # 合并对象（优先级：VirtualDisk > PhysicalDisk > Disk）
    merged = {}
    for disk in disks:
        key = get_unique_key(disk)
        if key not in merged:
            merged[key] = {'obj': disk.copy(), 'pool_names': set()}
        else:
            merged[key]['obj'].update(disk)

    for p in phys:
        key = get_unique_key(p)
        if key not in merged:
            merged[key] = {'obj': p.copy(), 'pool_names': set()}
        else:
            merged[key]['obj'].update(p)
        phys_id = p.get('ObjectId')
        if phys_id and str(phys_id) in phys_to_pool_uids:
            for uid in phys_to_pool_uids[str(phys_id)]:
                pool_name = pool_uid_to_name.get(str(uid))
                if pool_name:
                    merged[key]['pool_names'].add(pool_name)

    for v in virt:
        key = get_unique_key(v)
        if key not in merged:
            merged[key] = {'obj': v.copy(), 'pool_names': set()}
        else:
            merged[key]['obj'].update(v)
        virt_id = v.get('ObjectId')
        if virt_id and str(virt_id) in virt_to_pool_uids:
            for uid in virt_to_pool_uids[str(virt_id)]:
                pool_name = pool_uid_to_name.get(str(uid))
                if pool_name:
                    merged[key]['pool_names'].add(pool_name)

    # 根据 diskToVirt 映射补充池名称（通过虚拟磁盘间接关联）
    for disk in disks:
        disk_id = disk.get('ObjectId')
        if disk_id and str(disk_id) in disk_to_virt:
            virt_id = disk_to_virt[str(disk_id)]
            if virt_id in virt_to_pool_uids:
                key = get_unique_key(disk)
                if key in merged:
                    for uid in virt_to_pool_uids[virt_id]:
                        pool_name = pool_uid_to_name.get(str(uid))
                        if pool_name:
                            merged[key]['pool_names'].add(pool_name)

    # 构建最终磁盘列表，添加额外属性
    merged_disks = []
    for entry in merged.values():
        obj = entry['obj']
        obj['StoragePoolName'] = ','.join(sorted(entry['pool_names'])) if entry['pool_names'] else None
        # 添加 InterfaceType
        dev_id = obj.get('DeviceId')
        if dev_id is not None and str(dev_id) in interface_map:
            obj['InterfaceType'] = interface_map[str(dev_id)]
        else:
            obj['InterfaceType'] = None
        # 添加 FirmwareRevision
        if dev_id is not None and str(dev_id) in firmware_map:
            obj['FirmwareRevision'] = firmware_map[str(dev_id)]
        else:
            obj['FirmwareRevision'] = None
        merged_disks.append(obj)

    # 解析物理位置
    for disk in merged_disks:
        loc = disk.get('PhysicalLocation')
        parsed = parse_physical_location(loc)
        disk['LocationPrefix'] = parsed.get('Prefix', '')
        disk['BusNumber'] = parsed.get('Bus', '')
        disk['DeviceNumber'] = parsed.get('Device', '')
        disk['FunctionNumber'] = parsed.get('Function', '')
        disk['AdapterNumber'] = parsed.get('Adapter', '')
        disk['PortNumber'] = parsed.get('Port', '')
        disk['TargetId'] = parsed.get('Target', '')
        disk['Lun'] = parsed.get('LUN', '')

    # 按 YAML 配置提取值并后处理
    results = {}
    for it in target_items:
        rid = it['id']
        cfg = it['methods']['adv_disk']
        prop_list = cfg.get('properties', [])
        if not prop_list and 'property' in cfg:
            prop_list = [cfg['property']]
        default_val = cfg.get('default')
        post_steps = cfg.get('post', [])

        # 扁平化后处理步骤
        flattened = []
        for step in post_steps:
            if isinstance(step, list):
                flattened.extend(step)
            else:
                flattened.append(step)

        vals = []
        for disk in merged_disks:
            val = None
            for prop in prop_list:
                v = disk.get(prop)
                if v is not None:
                    val = v
                    break
            if val is None and default_val is not None:
                val = default_val
            vals.append(val)

        try:
            processed = post_process(vals, flattened)
            err_msg = ''
        except Exception as e:
            processed = vals
            err_msg = f"后处理异常: {str(e)[:100]}"

        ok_flag = is_valid(processed)
        results[rid] = {
            'raw': vals,
            'proc': processed,
            'ok': ok_flag,
            'err': err_msg,
            'warn': ''
        }
    return results

def collect_adv_vol(items: List[Dict], timeout: int) -> Dict[str, Dict]:
    target_items = [it for it in items if 'adv_vol' in it['methods']]
    if not target_items:
        return {}

    script = f'''
$exclude = @({', '.join(f"'{e}'" for e in EXCLUDE_ATTRS)})

function Get-CimInstances($className, $namespace) {{
    Get-CimInstance -ClassName $className -Namespace $namespace -ErrorAction SilentlyContinue | ForEach-Object {{
        $hash = @{{}}
        $_.PSObject.Properties | Where-Object {{ $_.Name -notin $exclude }} | ForEach-Object {{
            $hash[$_.Name] = $_.Value
        }}
        [PSCustomObject]$hash
    }}
}}

$parts = Get-CimInstances "MSFT_Partition" "root/microsoft/windows/storage"
$vols  = Get-CimInstances "MSFT_Volume" "root/microsoft/windows/storage"
$wmi   = Get-CimInstances "Win32_LogicalDisk" "root/cimv2"

@{{
    partitions = $parts
    volumes = $vols
    logicaldisks = $wmi
}} | ConvertTo-Json -Depth 3 -Compress
'''
    ok, data, script_err, json_err, ms = exec_batch([script], timeout)
    if not ok:
        print(f"  PowerShell执行失败: {script_err}")
        return {}
    if json_err:
        print(f"  JSON解析警告: {json_err}")

    if not data or not isinstance(data, dict):
        print("  返回数据格式错误")
        return {}

    parts = data.get('partitions', []) or []
    vols = data.get('volumes', []) or []
    wmis = data.get('logicaldisks', []) or []

    part_by_drive = {}
    part_by_guid = {}
    for p in parts:
        if not isinstance(p, dict):
            continue
        guid = p.get('Guid')
        if guid:
            part_by_guid[guid.upper()] = p
        drive = p.get('DriveLetter')
        if drive and isinstance(drive, str):
            part_by_drive[drive.rstrip(':').upper()] = p

    guid_pattern = re.compile(r'[{(]?([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})[})]?')
    vol_by_drive = {}
    vol_by_guid = {}
    for v in vols:
        if not isinstance(v, dict):
            continue
        drive = v.get('DriveLetter')
        if drive and isinstance(drive, str):
            vol_by_drive[drive.rstrip(':').upper()] = v
        unique_id = v.get('UniqueId', '')
        if isinstance(unique_id, str):
            m = guid_pattern.search(unique_id)
            if m:
                vol_by_guid[m.group(1).upper()] = v

    wmi_by_drive = {}
    for w in wmis:
        if not isinstance(w, dict):
            continue
        dev = w.get('DeviceID')
        if dev and isinstance(dev, str):
            wmi_by_drive[dev.rstrip(':').upper()] = w

    merged_items = []
    seen_keys = set()

    for p in parts:
        if not isinstance(p, dict):
            continue
        guid = p.get('Guid')
        drive = p.get('DriveLetter')
        if drive:
            drive = drive.upper()
        obj_id = p.get('ObjectId')
        key = guid or drive or obj_id
        if key in seen_keys:
            continue
        seen_keys.add(key)

        if guid and guid.upper() in vol_by_guid:
            base = dict(p)
            vol = vol_by_guid[guid.upper()]
            for k, v in vol.items():
                if v is not None:
                    base[k] = v
        elif drive and drive in vol_by_drive:
            base = dict(p)
            vol = vol_by_drive[drive]
            for k, v in vol.items():
                if v is not None:
                    base[k] = v
        else:
            base = dict(p)

        if drive and drive in wmi_by_drive:
            wmi = wmi_by_drive[drive]
            for k, v in wmi.items():
                if v is not None and k not in base:
                    base[k] = v

        merged_items.append(base)

    processed_drives = set()
    for item in merged_items:
        d = item.get('DriveLetter')
        if d:
            processed_drives.add(d.upper())
        dev = item.get('DeviceID')
        if dev and isinstance(dev, str):
            processed_drives.add(dev.rstrip(':').upper())

    for w in wmis:
        if not isinstance(w, dict):
            continue
        dev = w.get('DeviceID')
        if not dev:
            continue
        drive = dev.rstrip(':').upper()
        if drive in processed_drives:
            continue
        merged_items.append(dict(w))
        processed_drives.add(drive)

    item_ids = [obj.get('DriveLetter') or obj.get('DeviceID') or obj.get('ObjectId') or f"Obj{i}" for i, obj in enumerate(merged_items)]

    results = {}
    success_count = 0
    for it in target_items:
        rid = it['id']
        cfg = it['methods']['adv_vol']
        prop_list = cfg['properties']
        default_val = cfg.get('default')
        post_steps = cfg.get('post', [])

        flattened = []
        for step in post_steps:
            if isinstance(step, list):
                flattened.extend(step)
            else:
                flattened.append(step)

        vals = []
        for obj in merged_items:
            val = None
            for prop in prop_list:
                v = obj.get(prop)
                if v is not None:
                    val = v
                    break
            if val is None and default_val is not None:
                val = default_val
            vals.append(val)

        try:
            processed = post_process(vals, flattened)
            err_msg = ''
        except Exception as e:
            processed = vals
            err_msg = f"后处理异常: {str(e)[:100]}"

        ok_flag = is_valid(processed)
        if ok_flag:
            success_count += 1
        results[rid] = {
            'raw': vals,
            'proc': processed,
            'ok': ok_flag,
            'err': err_msg,
            'warn': ''
        }
    return results

def collect_adv_pool(items: List[Dict], timeout: int) -> Dict[str, Dict]:
    """
    采集存储池和存储层信息（MSFT_StoragePool / MSFT_StorageTier）
    统一使用 type + properties 配置，保持与 adv_disk/adv_vol 一致
    """
    target_items = [it for it in items if 'adv_pool' in it['methods']]
    if not target_items:
        return {}

    # 收集所有需要的属性（用于限制返回字段，避免序列化问题）
    all_props = set()
    for it in target_items:
        cfg = it['methods']['adv_pool']
        props = cfg.get('properties', [])
        for p in props:
            if p:
                all_props.add(p)
    # 添加必需字段
    required_props = {'FriendlyName', 'UniqueId', 'ObjectId', '_ObjectType'}
    all_props.update(required_props)
    prop_list = list(all_props)
    prop_list_str = ', '.join(f"'{p}'" for p in prop_list)

    # 构建 PowerShell 脚本
    exclude_list = ', '.join(f"'{e}'" for e in EXCLUDE_ATTRS)
    script = f'''
$props = @({prop_list_str})  # 需要返回的属性列表

function Get-FilteredObject($instance, $className) {{
    $hash = @{{}}
    $props | ForEach-Object {{
        $propName = $_
        if ($null -ne $instance.$propName) {{
            $hash[$propName] = $instance.$propName
        }}
    }}
    # 添加对象类型标识
    $hash["_ObjectType"] = "$className" -replace "MSFT_", ""
    [PSCustomObject]$hash
}}

# 获取存储池和存储层
$pools = Get-CimInstance -Namespace root/microsoft/windows/storage -ClassName MSFT_StoragePool -ErrorAction SilentlyContinue
$tiers = Get-CimInstance -Namespace root/microsoft/windows/storage -ClassName MSFT_StorageTier -ErrorAction SilentlyContinue

# 过滤属性并合并
$result = @()
$pools | ForEach-Object {{ $result += Get-FilteredObject $_ "MSFT_StoragePool" }}
$tiers | ForEach-Object {{ $result += Get-FilteredObject $_ "MSFT_StorageTier" }}

# 返回合并列表
$result | ConvertTo-Json -Depth 3 -Compress
'''
    ok, data, script_err, json_err, ms = exec_batch([script], timeout)
    if not ok:
        print(f"  PowerShell执行失败: {script_err}")
        return {}
    if json_err:
        print(f"  JSON解析警告: {json_err}")

    # data 应为列表，每个元素是字典
    objects = data if isinstance(data, list) else []

    results = {}
    for it in target_items:
        rid = it['id']
        cfg = it['methods']['adv_pool']
        obj_type = cfg.get('type', 'both')  # 'pool', 'tier', 'both'
        prop_list = cfg.get('properties', [])
        default_val = cfg.get('default')
        post_steps = cfg.get('post', [])

        # 扁平化后处理步骤
        flattened = []
        for step in post_steps:
            if isinstance(step, list):
                flattened.extend(step)
            else:
                flattened.append(step)

        # 根据类型过滤
        filtered = []
        for obj in objects:
            t = obj.get('_ObjectType', '')
            if obj_type == 'pool' and t != 'StoragePool':
                continue
            if obj_type == 'tier' and t != 'StorageTier':
                continue
            filtered.append(obj)

        # 提取属性值
        vals = []
        for obj in filtered:
            val = None
            for prop in prop_list:
                v = obj.get(prop)
                if v is not None:
                    val = v
                    break
            if val is None and default_val is not None:
                val = default_val
            vals.append(val)

        # 后处理
        try:
            processed = post_process(vals, flattened)
            err_msg = ''
        except Exception as e:
            processed = vals
            err_msg = f"后处理异常: {str(e)[:100]}"

        ok_flag = is_valid(processed)
        results[rid] = {
            'raw': vals,
            'proc': processed,
            'ok': ok_flag,
            'err': err_msg,
            'warn': ''
        }
    return results

def run_collector(name: str, func, id_info: dict, *args, **kwargs):
    print(f"采集器{name:<12}采集中...", end='', flush=True)
    start = time.perf_counter()
    results = func(*args, **kwargs)
    elapsed = time.perf_counter() - start
    total = len(results)
    success = sum(1 for v in results.values() if v.get('ok'))
    print(f"采集结束，耗时 {elapsed:2.2f}秒，成功{success:>3}/{total:>3}项")
    if success < total:
        fail_items = []
        for rid, v in results.items():
            if not v.get('ok') and rid in id_info:
                fail_items.append(f"[{rid}] {id_info[rid][1]}")
        if fail_items:
            for f in fail_items:
                print(f"              {f} 采集失败")
    return results

def collect_all_data(items: List[Dict], timeout: int, args) -> Dict[str, Dict]:
    id_info = {it['id']: (it['category'], it['cn']) for it in items}
    
    all_raw = {}
    
    all_raw['cim'] = run_collector('CIM', collect_cim, id_info, items, timeout)
    all_raw['powershell'] = run_collector('PowerShell', collect_ps, id_info, items, timeout)
    all_raw['reg'] = run_collector('REG', collect_reg, id_info, items)
    all_raw['psutil'] = run_collector('PsUtil', collect_psutil, id_info, items)
    all_raw['systeminfo'] = run_collector('SystemInfo', collect_systeminfo, id_info, items, timeout)
    all_raw['cpucache'] = run_collector('CPUcache', collect_cpucache, id_info, items)
    
    def collect_adv_graph_wrapped(items):
        gpu_list, monitor_list = collect_adv_graph(items)
        return process_adv_graph_items(items, gpu_list, monitor_list)
    
    all_raw['adv_graph'] = run_collector('adv_graph', collect_adv_graph_wrapped, id_info, items)
    
    all_raw['adv_cim'] = run_collector('adv_cim', collect_adv_cim, id_info, items, timeout)
    all_raw['adv_disk'] = run_collector('adv_disk', collect_adv_disk, id_info, items, timeout)
    all_raw['adv_vol'] = run_collector('adv_vol', collect_adv_vol, id_info, items, timeout)
    all_raw['adv_pool'] = run_collector('adv_pool', collect_adv_pool, id_info, items, timeout)
    
    return all_raw

def arbitrate_results(items: List[Dict], all_raw: Dict[str, Dict]) -> Tuple[Dict[str, Any], List[str]]:
    final = {}
    detail_lines = []
    
    for it in items:
        rid = it['id']
        merged = {}
        for mt in all_raw:
            if rid in all_raw[mt]:
                merged[mt] = all_raw[mt][rid]
        best_val = pick_best(rid, it, merged)
        final[rid] = best_val

        detail_lines.append(f"\n[{rid}]{it['category']}-{it['cn']} :")
        for mt in PRIORITY:
            if mt in merged:
                d = merged[mt]
                if mt == 'powershell' and 'commands' in d:
                    for cmd in d['commands']:
                        if cmd['status'] == 'SUCCESS':
                            status_char = '√'
                            info = cmd['proc'] if cmd['proc'] is not None else '<无值>'
                        elif cmd['status'] == 'NO_DATA':
                            status_char = 'Φ'
                            info = '<无数据>'
                        else:
                            status_char = '×'
                            info = cmd['error'] if cmd['error'] else '未知错误'
                        detail_lines.append(f"  {mt:>12} {status_char} {info}")
                else:
                    status = '√' if d['ok'] else '×'
                    info = d['proc'] if d['ok'] else d['err']
                    warn = d.get('warn', '')
                    warn_str = f" 警告:{warn}" if warn else ''
                    detail_lines.append(f"  {mt:>12} {status} {info}{warn_str}")
    
    return final, detail_lines

def print_formatted_output(items: List[Dict], final: Dict[str, Any]):
    def display_width(s):
        if s is None:
            return 4
        s = str(s)
        width = 0
        for ch in s:
            if ord(ch) > 127:
                width += 2
            else:
                width += 1
        return width

    groups = defaultdict(list)
    for it in items:
        if it.get('hide'):          # 跳过隐藏项
            continue
        groups[it['category']].append(it)

    for category, cat_items in groups.items():
        props_processed = []
        for it in cat_items:
            rid = it['id']
            name = it['cn']
            value = final.get(rid, None)
            if value is None:
                val_list = []
            elif isinstance(value, list):
                val_list = [str(v) if v is not None else '' for v in value]
            else:
                val_list = [str(value)]
            props_processed.append((name, val_list))

        if not props_processed:
            continue

        instance_count = max((len(val_list) for _, val_list in props_processed), default=1)
        if instance_count == 0:
            instance_count = 1

        for i, (name, val_list) in enumerate(props_processed):
            if len(val_list) < instance_count:
                val_list.extend([''] * (instance_count - len(val_list)))
                props_processed[i] = (name, val_list)

        global_col_widths = [0] * instance_count
        for _, val_list in props_processed:
            for i, v in enumerate(val_list):
                w = display_width(v)
                if w > global_col_widths[i]:
                    global_col_widths[i] = w

        start = 0
        group_index = 0
        while start < instance_count:
            group_indices = []
            current_width = 0
            name_width = max(display_width(name) for name, _ in props_processed)
            base_width = name_width + 2

            for i in range(start, instance_count):
                col_width = global_col_widths[i]
                if not group_indices:
                    new_width = base_width + col_width
                else:
                    new_width = current_width + 2 + col_width
                if new_width <= OUTPUT_MAX_WIDTH:
                    group_indices.append(i)
                    current_width = new_width
                else:
                    break
            if not group_indices:
                group_indices = [start]
                current_width = base_width + global_col_widths[start]

            if group_index == 0:
                print(f"\n----------------{category}----------------")
            else:
                print()

            group_col_widths = [0] * len(group_indices)
            for _, val_list in props_processed:
                for j, idx in enumerate(group_indices):
                    w = display_width(val_list[idx])
                    if w > group_col_widths[j]:
                        group_col_widths[j] = w

            for name, val_list in props_processed:
                group_values = [val_list[idx] for idx in group_indices]
                name_padding = name_width - display_width(name)
                name_padded = name + ' ' * name_padding
                padded_values = []
                for j, val in enumerate(group_values):
                    val_padding = group_col_widths[j] - display_width(val)
                    padded_values.append(val + ' ' * val_padding)
                line = name_padded + '  ' + '  '.join(padded_values)
                print(line)

            start = group_indices[-1] + 1
            group_index += 1

class Tee:
    def __init__(self, *files):
        self.files = files
    def write(self, obj):
        for f in self.files:
            f.write(obj)
            f.flush()
    def flush(self):
        for f in self.files:
            f.flush()

def setup_output_tee(filename_base: str, original_stdout):
    txt_path = f"{filename_base}.txt"
    txt_file = open(txt_path, 'w', encoding='utf-8')
    sys.stdout = Tee(original_stdout, txt_file)
    return txt_file

def output_results(items: List[Dict], final: Dict[str, Any], detail_lines: List[str], 
                   args, filename_base: str = None, should_export: bool = False):
    if args.debug:
        print("\n\n================采集状态================")
        print('\n'.join(detail_lines))
    
    if args.debug:
        print("\n\n================采集结果================")
        for it in items:
            rid = it['id']
            val = final.get(rid)
            prefix = f"{it['category']}-{it['cn']}:"
            if val is None:
                print(f"{prefix} <未获取>")
            elif isinstance(val, list):
                print(prefix)
                for v in val:
                    print(f"  {v}" if v is not None else "  ")
            else:
                print(f"{prefix} {val}")
    
    print("\n\n================格式输出================")
    print_formatted_output(items, final)
    
    if should_export and filename_base:
        export_to_csv(filename_base, items, final)

def export_to_csv(filename_base: str, items: List[Dict], final: Dict[str, Any]):
    csv_path = f"{filename_base}.csv"
    rows = []
    max_instances = 0
    
    # 只导出非隐藏项
    visible_items = [it for it in items if not it.get('hide', False)]

    
    for it in items:
        rid = it['id']
        val = final.get(rid)
        if val is None:
            vals = []
        elif isinstance(val, list):
            vals = [str(v) if v is not None else '' for v in val]
        else:
            vals = [str(val)]
        max_instances = max(max_instances, len(vals))
        attr_name = f"{it['category']}-{it['cn']}"
        rows.append((attr_name, vals))
    
    with open(csv_path, 'w', newline='', encoding='utf-8-sig') as f:
        writer = csv.writer(f)
        writer.writerow(['属性'] + [f'实例{i+1}' for i in range(max_instances)])
        for attr_name, vals in rows:
            row = [attr_name] + vals + [''] * (max_instances - len(vals))
            writer.writerow(row)
    
    print(f"\nCSV文件已导出: {csv_path}")

def handle_web_mode(items: List[Dict], final: Dict[str, Any], original_stdout):
    json_data = {}
    for it in items:
        key = f"{it['category']}-{it['cn']}"
        val = final.get(it['id'])
        if val is None:
            json_data[key] = []
        elif isinstance(val, list):
            json_data[key] = [str(v) if v is not None else '' for v in val]
        else:
            json_data[key] = [str(val)]
    sys.stdout = original_stdout
    print(json.dumps(json_data, ensure_ascii=False, indent=2))

def parse_arguments():
    parser = argparse.ArgumentParser(formatter_class=argparse.RawDescriptionHelpFormatter)    
    parser.add_argument('--config', '-c',default=DEFAULT_CONFIG,metavar='FILE')    
    parser.add_argument('--timeout', '-t',type=int, default=DEFAULT_TIMEOUT,metavar='SECONDS')    
    parser.add_argument('--debug',action='store_true')    
    parser.add_argument('--no-export',action='store_true')
    parser.add_argument('--web', action='store_true')
    return parser.parse_args()

def pick_best(item_id: str, item_cfg: Dict, method_results: Dict[str, Dict]) -> Any:
    valid = []
    for mt in PRIORITY:
        if mt in method_results and method_results[mt].get('ok'):
            valid.append((mt, method_results[mt]['proc']))
    if not valid:
        return None
    if len(valid) == 1:
        return valid[0][1]

    first = valid[0][1]
    all_eq = True
    for _, v in valid[1:]:
        if isinstance(first, list) and isinstance(v, list):
            if len(first) != len(v) or any(x != y for x, y in zip(first, v)):
                all_eq = False
                break
        elif str(first) != str(v):
            all_eq = False
            break
    if not all_eq:
        print(f"警告: [{item_id}]{item_cfg['category']}-{item_cfg['cn']} 存在不一致的采集值，已采用 {valid[0][0]} 来源")
    return valid[0][1]

def get_filename_base():
    computer_name = socket.gethostname()
    safe_name = re.sub(r'[\\/*?:"<>|]', '_', computer_name)
    timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    return f"{safe_name}_{timestamp}"

def collect_info(config_path: str, timeout: int = DEFAULT_TIMEOUT, debug: bool = False) -> Dict[str, Any]:
    anchors, items = load_config(config_path)
    args = type('Args', (), {'debug': debug})()
    all_raw = collect_all_data(items, timeout, args)
    final, _ = arbitrate_results(items, all_raw)
    return final

def main():
    
    print_usage_guide()
    args = parse_arguments()
    original_stdout = sys.stdout

    if args.web:
        sys.stdout = sys.stderr

    CoInitialize()
    txt_file = None
    try:
        if args.web:
            final = collect_info(args.config, args.timeout, args.debug)
            anchors, items = load_config(args.config)
            handle_web_mode(items, final, original_stdout)
            return

        should_export = not args.no_export
        filename_base = get_filename_base() if should_export else None
        if should_export:
            txt_file = setup_output_tee(filename_base, original_stdout)
            print(f"TXT文件将写入: {filename_base}.txt")
            print(f"CSV文件将写入: {filename_base}.csv")
        else:
            print("文件导出已禁用")

        anchors, items = load_config(args.config)
        all_raw = collect_all_data(items, args.timeout, args)
        final, detail_lines = arbitrate_results(items, all_raw)

        output_results(items, final, detail_lines, args, filename_base, should_export)

    finally:
        if not args.web and should_export and txt_file:
            sys.stdout = original_stdout
            txt_file.close()
            print(f"TXT文件已导出: {filename_base}.txt")
        gc.collect()
        CoUninitialize()

if __name__ == '__main__':
    main()