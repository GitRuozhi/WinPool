#!/usr/bin/env python3
"""
dxdiag 显示器与 GPU 信息提取工具
同时提取所有显示器信息和 GPU 详细信息，以 Monitor Id 作为显示器唯一标识，以 PCI ID 组合作为 GPU 唯一标识。
"""

import subprocess
import os
import sys
import re
from collections import defaultdict
from typing import List, Dict, Any, Tuple


def run_dxdiag_and_save(output_file: str = "dxdiag_record.txt") -> str | None:
    """运行 dxdiag 并将输出保存到文件，返回文件内容"""
    print("正在运行 dxdiag...")
    if os.path.exists(output_file):
        os.remove(output_file)

    try:
        result = subprocess.run(
            ['dxdiag', '/t', output_file],
            capture_output=True,
            text=True,
            timeout=120,
            creationflags=subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
        )
        if result.returncode != 0:
            print(f"dxdiag 运行失败 (错误码: {result.returncode})")
            return None
        print(f"dxdiag 输出已保存到: {output_file}")

        with open(output_file, 'r', encoding='utf-8', errors='ignore') as f:
            return f.read()
    except Exception as e:
        print(f"运行 dxdiag 出错: {e}")
        return None


def parse_all(content: str) -> Tuple[List[Dict[str, str]], List[Dict[str, str]]]:
    """
    解析 dxdiag 内容，提取所有显示器信息和 GPU 信息
    返回 (显示器列表, GPU 列表)
    """
    if not content:
        print("错误: dxdiag 内容为空")
        return [], []

    # 以 "Card name:" 分割，每段对应一个显卡/显示器条目
    sections = content.split("Card name:")
    if len(sections) < 2:
        print("未找到任何显卡信息")
        return [], []

    displays: List[Dict[str, str]] = []
    gpus_dict: Dict[str, Dict[str, str]] = {}

    # 跳过第一个部分（分割前的内容）
    for section in sections[1:]:
        lines = section.strip().split('\n')
        if not lines:
            continue

        # 显卡名称（第一行）
        card_name = lines[0].strip()
        if not card_name:
            card_name = "Unknown"

        # 初始化显示器信息
        disp: Dict[str, str] = {
            '型号': 'Unknown',
            'ID': 'Unknown',
            '连接显卡': card_name,
            '接口': 'Unknown',
            '默认分辨率': 'Unknown',
            '默认刷新率': 'Unknown',
            '输出分辨率': 'Unknown',
            '输出刷新率': 'Unknown',
            'HDR支持': 'Unknown',
            '颜色空间': 'Unknown',
            '桌面色深': 'Unknown',
        }

        # 临时存储当前显卡的信息（可能重复，后续会去重）
        gpu: Dict[str, str] = {
            '名称': card_name,
            '制造商': 'Unknown',
            '核心型号': 'Unknown',
            'Vendor ID': 'Unknown',
            'Device ID': 'Unknown',
            'SubSys ID': 'Unknown',
            '专用显存': 'Unknown',
            '共享显存': 'Unknown',
            '合计显存': 'Unknown',
            '驱动版本号': 'Unknown',
            'DirectX版本': 'Unknown',
            '硬件编码列表': 'Unknown',
            '硬件解码列表': 'Unknown',
        }

        # 逐行解析该部分
        for line in lines:
            line = line.strip()
            if not line:
                continue

            # ----- 显示器相关字段 -----
            if line.startswith("Monitor Model:"):
                disp['型号'] = line.replace("Monitor Model:", "").strip()
            elif line.startswith("Monitor Id:"):
                disp['ID'] = line.replace("Monitor Id:", "").strip()
            elif line.startswith("Current Mode:"):
                # 示例: 1536 x 2048 (32 bit) (60Hz)
                current_match = re.search(
                    r'Current Mode:\s*(\d+)\s*x\s*(\d+)\s*\((\d+)\s*bit\)\s*\(([\d.]+)Hz\)',
                    line
                )
                if current_match:
                    w, h, bits, hz = current_match.groups()
                    disp['输出分辨率'] = f"{w}x{h}"
                    disp['输出刷新率'] = f"{hz}Hz"
                    disp['桌面色深'] = f"{bits}BPP"
                else:
                    disp['输出分辨率'] = line.replace("Current Mode:", "").strip()
            elif line.startswith("Native Mode:"):
                native_match = re.search(
                    r'Native Mode:\s*(\d+)\s*x\s*(\d+)(?:\(p\))?\s*\(([\d.]+)Hz\)',
                    line
                )
                if native_match:
                    w, h, hz = native_match.groups()
                    disp['默认分辨率'] = f"{w}x{h}"
                    disp['默认刷新率'] = f"{hz}Hz"
                else:
                    disp['默认分辨率'] = line.replace("Native Mode:", "").strip()
            elif line.startswith("Output Type:"):
                disp['接口'] = line.replace("Output Type:", "").strip().upper()
            elif line.startswith("HDR Support:"):
                hdr_text = line.replace("HDR Support:", "").strip()
                if hdr_text.lower() in ("supported", "yes", "true"):
                    disp['HDR支持'] = "支持"   # 稍后可能被 Capabilities 覆盖
                else:
                    disp['HDR支持'] = "不支持"
            elif line.startswith("Monitor Capabilities:"):
                caps = line.replace("Monitor Capabilities:", "").strip()
                formats = []
                for fmt in ["BT2020RGB", "BT2020YCC", "Eotf2084Supported", "HDR10"]:
                    if fmt in caps:
                        formats.append(fmt)
                if formats:
                    disp['HDR支持'] = ", ".join(formats)
                elif disp['HDR支持'] == "支持":
                    disp['HDR支持'] = "支持（格式Unknown）"
            elif line.startswith("Display Color Space:"):
                color = line.replace("Display Color Space:", "").strip().upper()
                if "RGB" in color:
                    disp['颜色空间'] = "RGB"
                elif "YCBCR" in color or "YUV" in color:
                    disp['颜色空间'] = "YUV"
                else:
                    disp['颜色空间'] = color

            # ----- GPU 相关字段 -----
            elif line.startswith("Manufacturer:"):
                gpu['制造商'] = line.replace("Manufacturer:", "").strip()
            elif line.startswith("Chip type:"):
                gpu['核心型号'] = line.replace("Chip type:", "").strip()
            elif line.startswith("Vendor ID:"):
                gpu['Vendor ID'] = line.replace("Vendor ID:", "").strip()
            elif line.startswith("Device ID:"):
                gpu['Device ID'] = line.replace("Device ID:", "").strip()
            elif line.startswith("SubSys ID:"):
                gpu['SubSys ID'] = line.replace("SubSys ID:", "").strip()
            elif line.startswith("Dedicated Memory:"):
                gpu['专用显存'] = line.replace("Dedicated Memory:", "").strip()
            elif line.startswith("Shared Memory:"):
                gpu['共享显存'] = line.replace("Shared Memory:", "").strip()
            elif line.startswith("Display Memory:"):
                gpu['合计显存'] = line.replace("Display Memory:", "").strip()
            elif line.startswith("Driver Version:"):
                gpu['驱动版本号'] = line.replace("Driver Version:", "").strip()
            elif line.startswith("DDI Version:"):
                gpu['DirectX版本'] = line.replace("DDI Version:", "").strip()
            elif line.startswith("Video Accel:"):
                # 硬件编码列表 (通常由空格分隔)
                accel = line.replace("Video Accel:", "").strip()
                gpu['硬件编码列表'] = accel if accel else "无"
            elif line.startswith("DXVA2 Modes:"):
                # 硬件解码列表 (可能包含 GUID 和名称)
                modes = line.replace("DXVA2 Modes:", "").strip()
                # 简化显示：保留原始内容，或者提取常见名称（这里保留原始）
                gpu['硬件解码列表'] = modes if modes else "无"

        displays.append(disp)

        # 构造 GPU 唯一标识：优先使用 Vendor+Device+SubSys 的组合
        gpu_id_parts = [gpu['Vendor ID'], gpu['Device ID'], gpu['SubSys ID']]
        if all(part != 'Unknown' for part in gpu_id_parts):
            gpu_id = "|".join(gpu_id_parts)
        else:
            # 备用方案：使用显卡名称（可能不唯一，但至少区分不同型号）
            gpu_id = card_name

        # 去重：如果该 GPU 尚未记录，则加入字典
        if gpu_id not in gpus_dict:
            gpus_dict[gpu_id] = gpu

    return displays, list(gpus_dict.values())


def main() -> None:
    print("dxdiag 显示器与 GPU 信息提取工具")
    print("=" * 60)

    dxdiag_content = run_dxdiag_and_save()
    if not dxdiag_content:
        print("无法获取 dxdiag 内容，退出程序")
        return

    print(f"dxdiag 内容长度: {len(dxdiag_content)} 字符")

    displays, gpus = parse_all(dxdiag_content)

    # 输出显示器信息
    if displays:
        print(f"\n检测到 {len(displays)} 个显示器:")
        print("=" * 60)
        for i, disp in enumerate(displays, 1):
            print(f"\n显示器 {i}:")
            print(f"  型号: {disp['型号']}")
            print(f"  ID: {disp['ID']}")
            print(f"  连接显卡: {disp['连接显卡']}")
            print(f"  接口: {disp['接口']}")
            print(f"  默认分辨率: {disp['默认分辨率']}")
            print(f"  默认刷新率: {disp['默认刷新率']}")
            print(f"  输出分辨率: {disp['输出分辨率']}")
            print(f"  输出刷新率: {disp['输出刷新率']}")
            print(f"  HDR 支持: {disp['HDR支持']}")
            print(f"  颜色空间: {disp['颜色空间']}")
            print(f"  桌面色深: {disp['桌面色深']}")
    else:
        print("\n未检测到显示器信息")

    # 输出 GPU 信息
    if gpus:
        print(f"\n检测到 {len(gpus)} 个 GPU:")
        print("=" * 60)
        for i, gpu in enumerate(gpus, 1):
            print(f"\nGPU {i}:")
            print(f"  名称: {gpu['名称']}")
            print(f"  制造商: {gpu['制造商']}")
            print(f"  核心型号: {gpu['核心型号']}")
            print(f"  Vendor ID: {gpu['Vendor ID']}")
            print(f"  Device ID: {gpu['Device ID']}")
            print(f"  SubSys ID: {gpu['SubSys ID']}")
            print(f"  专用显存: {gpu['专用显存']}")
            print(f"  共享显存: {gpu['共享显存']}")
            print(f"  合计显存: {gpu['合计显存']}")
            print(f"  驱动版本号: {gpu['驱动版本号']}")
            print(f"  DirectX 版本: {gpu['DirectX版本']}")
            print(f"  硬件编码列表: {gpu['硬件编码列表']}")
            print(f"  硬件解码列表: {gpu['硬件解码列表']}")
    else:
        print("\n未检测到 GPU 信息")

    # 统计信息
    print("\n原始 dxdiag 统计:")
    print("-" * 60)
    print(f"文件大小: {len(dxdiag_content)} 字符")
    print(f"Card name 出现次数: {dxdiag_content.count('Card name:')}")
    print(f"Monitor Model 出现次数: {dxdiag_content.count('Monitor Model:')}")
    print(f"Current Mode 出现次数: {dxdiag_content.count('Current Mode:')}")


if __name__ == "__main__":
    main()