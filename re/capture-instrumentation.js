// Paste into DevTools console on https://hed.aulacn.com/ BEFORE clicking "Allow browser access".
// Then: window.__hidClear() before each isolated test, window.__hidDump() or the
// blob-download snippet below to export after.
(function(){
  if (!navigator.hid) { console.warn("no navigator.hid"); return; }
  window.__hidLog = [];
  function buf2hex(buf) {
    let arr;
    if (buf instanceof ArrayBuffer) arr = new Uint8Array(buf);
    else if (buf instanceof DataView) arr = new Uint8Array(buf.buffer, buf.byteOffset, buf.byteLength);
    else if (ArrayBuffer.isView(buf)) arr = new Uint8Array(buf.buffer, buf.byteOffset, buf.byteLength);
    else arr = new Uint8Array(0);
    return Array.from(arr).map(b=>b.toString(16).padStart(2,'0')).join(' ');
  }
  function log(entry){
    entry.t = performance.now().toFixed(2);
    window.__hidLog.push(entry);
    if (window.__hidLog.length > 20000) window.__hidLog.shift();
  }
  const origRequestDevice = navigator.hid.requestDevice.bind(navigator.hid);
  navigator.hid.requestDevice = async function(opts){
    log({type:'requestDevice', opts: JSON.stringify(opts)});
    const devices = await origRequestDevice(opts);
    devices.forEach(patchDevice);
    log({type:'requestDevice:result', devices: devices.map(d=>({vendorId:d.vendorId.toString(16),productId:d.productId.toString(16),productName:d.productName}))});
    return devices;
  };
  navigator.hid.getDevices().then(devs => devs.forEach(patchDevice));
  const patched = new WeakSet();
  function patchDevice(dev){
    if (patched.has(dev)) return;
    patched.add(dev);
    const origOpen = dev.open.bind(dev);
    dev.open = async function(){
      log({type:'open', vendorId:dev.vendorId.toString(16), productId:dev.productId.toString(16)});
      const r = await origOpen();
      log({type:'open:done', collections: JSON.stringify(dev.collections)});
      return r;
    };
    const origSendReport = dev.sendReport.bind(dev);
    dev.sendReport = async function(reportId, data){
      log({type:'sendReport', reportId, data: buf2hex(data), len: data.byteLength});
      return origSendReport(reportId, data);
    };
    const origSendFeatureReport = dev.sendFeatureReport.bind(dev);
    dev.sendFeatureReport = async function(reportId, data){
      log({type:'sendFeatureReport', reportId, data: buf2hex(data), len: data.byteLength});
      return origSendFeatureReport(reportId, data);
    };
    const origReceiveFeatureReport = dev.receiveFeatureReport.bind(dev);
    dev.receiveFeatureReport = async function(reportId){
      const r = await origReceiveFeatureReport(reportId);
      log({type:'receiveFeatureReport', reportId, data: buf2hex(r), len: r.byteLength});
      return r;
    };
    dev.addEventListener('inputreport', (e) => {
      log({type:'inputreport', reportId: e.reportId, data: buf2hex(e.data)});
    });
  }
  navigator.hid.addEventListener('connect', (e)=>{ log({type:'connect-event'}); patchDevice(e.device); });
  window.__hidDump = () => JSON.stringify(window.__hidLog, null, 1);
  window.__hidClear = () => { window.__hidLog = []; };
  window.__hidDownload = (name) => {
    const blob = new Blob([JSON.stringify(window.__hidLog, null, 1)], {type: 'application/json'});
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = (name || 'aula_hid_dump') + '.json';
    document.body.appendChild(a);
    a.click();
    a.remove();
  };
  console.log("WebHID instrumentation installed. window.__hidClear() / window.__hidDownload('name') ready.");
})();
