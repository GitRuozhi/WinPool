import sys
import os
import webbrowser
from threading import Timer
from flask import Flask, jsonify, render_template
import StatSys

def resource_path(relative_path):
    if getattr(sys, 'frozen', False):
        base_path = os.path.dirname(sys.executable)
    else:
        base_path = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(base_path, relative_path)

app = Flask(__name__,
            template_folder=resource_path('templates'),
            static_folder=resource_path('static'))

CONFIG_PATH = resource_path('Full.yaml')
TIMEOUT = 30

@app.route('/')
def index():
    return render_template('index.html')

@app.route('/api/hwinfo')
def hwinfo():
    try:
        result = StatSys.collect_info(CONFIG_PATH, TIMEOUT, debug=False)
        # 转换为前端格式
        anchors, items = StatSys.load_config(CONFIG_PATH)
        json_data = {}
        for it in items:
            key = f"{it['category']}-{it['cn']}"
            val = result.get(it['id'])
            if val is None:
                json_data[key] = []
            elif isinstance(val, list):
                json_data[key] = [str(v) if v is not None else '' for v in val]
            else:
                json_data[key] = [str(val)]
        return jsonify(json_data)
    except Exception as e:
        return jsonify({'error': str(e)}), 500

def open_browser():
    webbrowser.open_new('http://127.0.0.1:5000/')

if __name__ == '__main__':
    Timer(1, open_browser).start()
    app.run(host='127.0.0.1', port=5000, debug=False)