const path = require('path');
const express = require('express');
const { WebSocketServer } = require('ws');

const PORT = process.env.PORT || 3000;

const app = express();
app.use(express.static(path.join(__dirname, 'public')));

app.get('/health', (_req, res) => {
  res.json({ status: 'ok', clients: clients.size });
});

const server = app.listen(PORT, '0.0.0.0', () => {
  console.log(`[SignalingServer] listening on http://0.0.0.0:${PORT}`);
  console.log('[SignalingServer] point WebRTCConnection.WebSocketServerAddress at ws://<this-machine-lan-ip>:' + PORT);
});

const wss = new WebSocketServer({ server });

// SimpleWebRTC organizes peers itself via NEWPEER/NEWPEERACK broadcasts and
// filters targeted messages (OFFER/ANSWER/CANDIDATE/DATA) client-side by
// ReceiverPeerId, so the server only has to be a dumb broadcast relay - no
// sessions/rooms/roles to track here.
const clients = new Set();

wss.on('connection', (ws, req) => {
  clients.add(ws);
  console.log(`[SignalingServer] client connected (${clients.size} total) from ${req.socket.remoteAddress}`);

  ws.on('message', (data, isBinary) => {
    const messageType = isBinary ? '(binary)' : data.toString().split('|')[0];
    console.log(`[SignalingServer] relay ${messageType} to ${clients.size - 1} peer(s)`);

    for (const client of clients) {
      if (client !== ws && client.readyState === client.OPEN) {
        client.send(data, { binary: isBinary });
      }
    }
  });

  ws.on('close', () => {
    clients.delete(ws);
    console.log(`[SignalingServer] client disconnected (${clients.size} total)`);
  });

  ws.on('error', (err) => {
    console.error('[SignalingServer] socket error:', err.message);
  });
});
