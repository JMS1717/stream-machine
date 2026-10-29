// Sends a Wake-on-LAN magic packet (6 x 0xFF + 16 x MAC) to the broadcast address, ports 9 and 7.
var dgram = require('dgram');
var mac = (process.argv[2] || '').replace(/[^0-9a-fA-F]/g, '');
if (mac.length !== 12) { console.error('usage: node wol.js AA:BB:CC:DD:EE:FF'); process.exit(2); }
var m = Buffer.from ? Buffer.from(mac, 'hex') : new Buffer(mac, 'hex');
var p = Buffer.alloc ? Buffer.alloc(102, 0xff) : (function () { var b = new Buffer(102); b.fill(0xff); return b; })();
for (var i = 0; i < 16; i++) m.copy(p, 6 + i * 6);
var s = dgram.createSocket('udp4');
s.bind(function () {
    s.setBroadcast(true);
    var left = 6;
    [9, 7].forEach(function (port) {
        [0, 300, 900].forEach(function (delay) {
            setTimeout(function () {
                s.send(p, 0, p.length, port, '255.255.255.255', function (err) {
                    if (err) console.error(err.message);
                    if (--left === 0) { console.log('sent'); s.close(); }
                });
            }, delay);
        });
    });
});
