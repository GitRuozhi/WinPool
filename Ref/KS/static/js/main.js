document.getElementById('fetchBtn').addEventListener('click', function() {
    const loading = document.getElementById('loading');
    const resultDiv = document.getElementById('result');
    loading.style.display = 'block';
    resultDiv.innerHTML = '';

    fetch('/api/hwinfo')
        .then(response => response.json())
        .then(data => {
            loading.style.display = 'none';
            if (data.error) {
                resultDiv.innerHTML = `<p style="color:red;">错误：${data.error}</p>`;
                return;
            }
            // 将数据渲染为表格
            let html = '<table border="1" cellpadding="5" style="border-collapse: collapse;">';
            // 确定最大实例数（用于列数）
            let maxInstances = 1;
            for (let key in data) {
                if (data[key].length > maxInstances) maxInstances = data[key].length;
            }
            // 表头
            html += '<tr><th>属性</th>';
            for (let i = 1; i <= maxInstances; i++) {
                html += `<th>实例 ${i}</th>`;
            }
            html += '</tr>';
            // 数据行
            for (let key in data) {
                html += '<tr>';
                html += `<td>${key}</td>`;
                const values = data[key];
                for (let i = 0; i < maxInstances; i++) {
                    html += `<td>${values[i] !== undefined ? values[i] : ''}</td>`;
                }
                html += '</tr>';
            }
            html += '</table>';
            resultDiv.innerHTML = html;
        })
        .catch(error => {
            loading.style.display = 'none';
            resultDiv.innerHTML = `<p style="color:red;">请求失败：${error}</p>`;
        });
});