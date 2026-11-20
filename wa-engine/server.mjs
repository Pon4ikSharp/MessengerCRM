import express from 'express';
import QRCode from 'qrcode';
import fs from 'fs';
import wwebjs from 'whatsapp-web.js';

const { Client, LocalAuth } = wwebjs;
const PORT = 18952;
const AUTH_DIR = 'C:\\MessengerCRMServer\\data\\wwebjs-auth';
const TOKEN_PATH = 'C:\\MessengerCRMServer\\data\\engine-token.txt';
const CALLBACK_BASE = 'http://127.0.0.1:18950';
const CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';

fs.mkdirSync(AUTH_DIR, { recursive: true });
const engineToken = fs.readFileSync(TOKEN_PATH, 'utf8').trim();

let client = null;
let currentQr = null;
let connected = false;
let initializing = false;
let lastError = null;
let account = null;
let reconnectTimer = null;

function digitsOnly(v='') { return String(v).replace(/\D/g, ''); }
function jidToPhone(jid='') {
  const local = String(jid).split('@')[0].replace(/\D/g, '');
  return /^\d{8,15}$/.test(local) ? `+${local}` : '';
}

async function callback(pathName, payload) {
  try {
    await fetch(CALLBACK_BASE + pathName, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Engine-Token': engineToken },
      body: JSON.stringify(payload),
      signal: AbortSignal.timeout(7000)
    });
  } catch (e) {
    console.warn('callback failed', pathName, e?.message || e);
  }
}

function buildClient() {
  const c = new Client({
    authStrategy: new LocalAuth({ clientId: 'messengercrm', dataPath: AUTH_DIR }),
    puppeteer: {
      headless: true,
      executablePath: CHROME,
      args: [
        '--no-sandbox',
        '--disable-setuid-sandbox',
        '--disable-gpu',
        '--disable-dev-shm-usage',
        '--disable-background-networking',
        '--disable-features=Translate,MediaRouter'
      ]
    }
  });

  c.on('qr', qr => {
    currentQr = qr;
    connected = false;
    lastError = null;
  });

  c.on('authenticated', () => {
    lastError = null;
  });

  c.on('ready', () => {
    connected = true;
    initializing = false;
    currentQr = null;
    lastError = null;
    try {
      const wid = c.info?.wid?._serialized || c.info?.wid?.user || null;
      account = wid || c.info?.pushname || null;
    } catch { account = null; }
    console.log('WhatsApp Web.js ready', account || '');
  });

  c.on('auth_failure', msg => {
    connected = false;
    initializing = false;
    currentQr = null;
    lastError = `auth_failure: ${msg}`;
  });

  c.on('disconnected', reason => {
    connected = false;
    initializing = false;
    currentQr = null;
    lastError = `disconnected: ${reason}`;
    console.warn('WhatsApp disconnected', reason);
    clearTimeout(reconnectTimer);
    reconnectTimer = setTimeout(async () => {
      try { await resetAndInitialize(false); } catch {}
    }, 5000);
  });

  c.on('message', async msg => {
    try {
      if (!msg || msg.fromMe) return;
      const from = msg.from || '';
      if (!from || from.endsWith('@g.us') || from === 'status@broadcast') return;
      const text = String(msg.body || '').trim();
      if (!text) return;
      let phone = jidToPhone(from);
      if (!phone) {
        try {
          const contact = await msg.getContact();
          const number = digitsOnly(contact?.number || contact?.id?.user || '');
          if (number.length >= 8 && number.length <= 15) phone = `+${number}`;
        } catch {}
      }
      await callback('/internal/whatsapp/incoming', {
        jid: from,
        phone,
        messageId: msg.id?._serialized || null,
        text,
        timestamp: Number(msg.timestamp || 0)
      });
    } catch (e) {
      console.warn('incoming message handler failed', e?.message || e);
    }
  });

  c.on('message_ack', async (msg, ack) => {
    try {
      // whatsapp-web.js: 3 = READ, 4 = PLAYED.
      if (Number(ack) >= 3 && msg?.id?._serialized) {
        await callback('/internal/whatsapp/read', {
          jid: msg.to || msg.from || '',
          messageId: msg.id._serialized
        });
      }
    } catch (e) {
      console.warn('ack handler failed', e?.message || e);
    }
  });

  return c;
}

async function initialize() {
  if (connected || initializing) return;
  initializing = true;
  lastError = null;
  try {
    if (!client) client = buildClient();
    await client.initialize();
  } catch (e) {
    initializing = false;
    connected = false;
    lastError = String(e?.stack || e);
    console.error('WhatsApp initialize failed', lastError);
  }
}

async function resetAndInitialize(clearSession=false) {
  clearTimeout(reconnectTimer);
  try { if (client) await client.destroy(); } catch {}
  client = null;
  connected = false;
  initializing = false;
  currentQr = null;
  account = null;
  if (clearSession) {
    try { fs.rmSync(AUTH_DIR, { recursive: true, force: true }); } catch {}
    fs.mkdirSync(AUTH_DIR, { recursive: true });
  }
  await initialize();
}

async function ensureReady() {
  if (!connected || !client) throw new Error('WhatsApp is not connected');
}

const app = express();
app.use(express.json({ limit: '1mb' }));

app.get('/health', (req,res) => res.json({ ok: true, connected, qrReady: !!currentQr, initializing, error: lastError }));
app.get('/status', (req,res) => res.json({ connected, qrReady: !!currentQr, initializing, error: lastError, account }));
app.post('/start', async (req,res) => {
  await initialize();
  res.json({ connected, qrReady: !!currentQr, initializing, error: lastError, account });
});
app.post('/reset', async (req,res) => {
  await resetAndInitialize(true);
  res.json({ connected, qrReady: !!currentQr, initializing, error: lastError });
});
app.get('/qr.png', async (req,res) => {
  if (!currentQr) return res.status(409).json({ error: connected ? 'already_connected' : 'qr_not_ready' });
  try {
    const png = await QRCode.toBuffer(currentQr, { type: 'png', width: 460, margin: 4, errorCorrectionLevel: 'M' });
    res.type('png').send(png);
  } catch (e) {
    res.status(500).json({ error: String(e?.message || e) });
  }
});

app.post('/check', async (req,res) => {
  try {
    await ensureReady();
    const digits = digitsOnly(req.body?.phone);
    if (digits.length < 8 || digits.length > 15)
      return res.status(400).json({ state: 'no', detail: 'invalid phone' });
    const id = await client.getNumberId(digits);
    if (!id) return res.json({ state: 'no' });
    return res.json({ state: 'yes', jid: id._serialized || `${id.user}@${id.server}` });
  } catch (e) {
    return res.status(500).json({ state: 'unknown', detail: String(e?.message || e) });
  }
});

app.post('/save-contact', async (req,res) => {
  try {
    await ensureReady();
    const digits = digitsOnly(req.body?.phone);
    const firstName = String(req.body?.firstName || '').trim();
    const lastName = String(req.body?.lastName || '').trim();
    if (digits.length < 8 || digits.length > 15) return res.status(400).json({ error: 'invalid phone' });
    if (!firstName) return res.status(400).json({ error: 'empty contact name' });
    await client.saveOrEditAddressbookContact(digits, firstName, lastName, false);
    return res.json({ ok: true });
  } catch (e) {
    return res.status(500).json({ error: String(e?.message || e) });
  }
});

app.post('/contact-info', async (req,res) => {
  try {
    await ensureReady();
    const digits = digitsOnly(req.body?.phone);
    const id = await client.getNumberId(digits);
    if (!id) return res.status(404).json({ found: false });
    const jid = id._serialized || `${id.user}@${id.server}`;
    const c = await client.getContactById(jid);
    return res.json({ found: true, jid, name: c?.name || null, pushname: c?.pushname || null, isMyContact: !!c?.isMyContact });
  } catch (e) { return res.status(500).json({ error: String(e?.message || e) }); }
});

app.post('/send', async (req,res) => {
  try {
    await ensureReady();
    const digits = digitsOnly(req.body?.phone);
    const text = String(req.body?.text || '');
    const firstName = String(req.body?.firstName || '').trim();
    const lastName = String(req.body?.lastName || '').trim();
    if (!text.trim()) return res.status(400).json({ error: 'empty message' });
    if (firstName) {
      try { await client.saveOrEditAddressbookContact(digits, firstName, lastName, false); }
      catch (e) { console.warn('save contact before send failed', digits, e?.message || e); }
    }
    const id = await client.getNumberId(digits);
    if (!id) return res.status(404).json({ error: 'WhatsApp account not found for this phone' });
    const jid = id._serialized || `${id.user}@${id.server}`;
    const sent = await client.sendMessage(jid, text);
    return res.json({ jid, messageId: sent?.id?._serialized || null });
  } catch (e) {
    return res.status(500).json({ error: String(e?.message || e) });
  }
});

app.listen(PORT, '127.0.0.1', () => {
  console.log(`MessengerCRM WhatsApp Web.js engine listening on 127.0.0.1:${PORT}`);
  initialize().catch(()=>{});
});
