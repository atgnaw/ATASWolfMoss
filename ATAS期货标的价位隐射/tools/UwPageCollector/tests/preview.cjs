// Development-only visual preview; never packaged in the extension.
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '../extension');
http.createServer((req, res) => {
  const name = new URL(req.url, 'http://localhost').pathname.slice(1) || 'console.html';
  if (!['console.html','console.css','reasons.css','console.js','quality.js','store.js','preview-mock.js'].includes(name)) { res.writeHead(404); res.end(); return; }
  res.setHeader('Content-Type', name.endsWith('.html') ? 'text/html; charset=utf-8' : name.endsWith('.css') ? 'text/css' : 'text/javascript');
  let body = fs.readFileSync(name === 'preview-mock.js' ? path.join(__dirname, name) : path.join(root, name), 'utf8');
  if (name === 'console.html') body = body.replace('<script src="store.js">', '<script src="preview-mock.js"></script><script src="store.js">');
  res.end(body);
}).listen(8769, '127.0.0.1', () => console.log('UI-only preview at http://127.0.0.1:8769'));
