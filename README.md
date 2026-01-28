# Cloudflare / CDN Fast IP V2Ray Scanner

A **high-performance IP range scanner and V2Ray (Xray) connectivity tester** written in **.NET**.

This tool is designed to scan large IP ranges, validate them via **TCP port behavior**, and finally verify **real VLESS-WS-TLS connectivity** using Xray.

Only IPs that pass a **real end-to-end V2Ray connection test** are marked as alive.

---

## ✨ Key Features

- 🚀 Parallel TCP scanning with configurable concurrency  
- 🔍 Sequential **multi-port TCP validation per IP**
- 🔐 Real **VLESS + WebSocket + TLS** testing via Xray  
- 🧵 Producer–Consumer architecture using bounded channels  
- 📊 Live progress monitoring (speed, queue size, alive IPs)  
- 🧹 Automatic cleanup of temp configs and Xray processes  

---

## ⚠️ Important Requirement (Read This First)

**This program DOES NOT work out of the box.**

You **must already have a working VLESS-WS-TLS configuration**.

The scanner **reuses your own VLESS setup** to test candidate IPs.

That means:

- You need a **valid server**
- A **working VLESS-WS-TLS connection**
- Correct values set manually in `appsettings.json`

If your VLESS config does not work normally, this scanner will not magically fix it.

---

## 🔐 Required VLESS-WS-TLS Configuration

You must configure the following section in `appsettings.json`:

```json
"V2Ray": {
  "VlessUuid": "YOUR-UUID",
  "VlessSni": "your-sni-domain",
  "VlessHost": "your-host",
  "VlessPath": "/your/ws/path"
}
```

The scanner dynamically replaces only the **IP address** while keeping **your exact VLESS configuration** intact.

---

## 📄 Input File (`ip.txt`)

Supports single IPs and CIDR ranges.

---

## 📂 Output

- **alive_ip.txt** – confirmed working IPs only

---

## 📜 Disclaimer

For research and educational purposes only.

