import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
import {chromium} from 'playwright';

const theme = new URL('../themes/pond-garden/', import.meta.url);
test('desktop seed path, feedback, pause, and reload persistence', async () => {
  const server = createServer(async (req, res) => {
    const name = new URL(req.url, 'http://localhost').pathname.slice(1) || 'flower-meadow.html';
    if (!['flower-meadow.html','pond-botanical.webp','pond-botanical-atlas.webp'].includes(name)) { res.writeHead(404).end(); return; }
    res.setHeader('Content-Type', name.endsWith('.html') ? 'text/html; charset=utf-8' : 'image/webp');
    res.end(await readFile(new URL(name, theme)));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch();
    const page = await browser.newPage({viewport: {width:1512, height:982}});
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    await page.waitForFunction(() => window.meadowDiagnostics?.().ready);
    await page.evaluate(() => {
      meadowDesktop('wallpaper',1);
      meadowDesktop('windowsDown',190,690); meadowDesktop('up');
    });
    assert.equal(await page.evaluate(() => meadowDiagnostics().flowerCount),0,'idle desktop must not plant');
    const desktopClick = async selector => page.evaluate(selector => {
      const r = document.querySelector(selector).getBoundingClientRect();
      meadowDesktop('windowsDown',r.x+r.width/2,r.y+r.height/2); meadowDesktop('up');
    }, selector);
    await desktopClick('#basket');
    assert.equal(await page.locator('#seeds-panel').isVisible(),true);
    await desktopClick('#rack button:nth-child(2)');
    assert.equal(await page.locator('#seeds-panel').isVisible(),false);
    await page.evaluate(() => { meadowDesktop('windowsDown',190,690); meadowDesktop('up'); });
    assert.equal(await page.locator('#status').innerText(),'已种下雏菊。');
    assert.equal(await page.evaluate(() => meadowDiagnostics().flowerCount),1);
    const bounds = await page.locator('#status').boundingBox();
    assert.ok(bounds.y+bounds.height<905,'toast must be above the observed Mac Dock boundary');
    assert.equal(await page.evaluate(() => JSON.parse(localStorage.getItem('pond-garden-v2')).flowers[0].kind),1);
    await page.evaluate(() => { meadowDesktop('windowsDown',900,450); meadowDesktop('up'); });
    assert.equal(await page.evaluate(() => meadowDiagnostics().flowerCount),1,'water rejects planting');
    assert.equal(await page.evaluate(() => meadowDesktop('flush')),true);
    await page.evaluate(() => meadowDesktop('visible',0));
    const paused = await page.evaluate(() => meadowDiagnostics().frameCount);
    await new Promise(resolve => setTimeout(resolve,350));
    assert.equal(await page.evaluate(() => meadowDiagnostics().frameCount),paused,'hidden renderer stops frames');
    await page.evaluate(() => meadowDesktop('visible',1));
    await page.waitForFunction(n => meadowDiagnostics().frameCount>n,paused);
    await desktopClick('#planting-done');
    await page.evaluate(() => { meadowDesktop('windowsDown',240,690); meadowDesktop('up'); });
    assert.equal(await page.evaluate(() => meadowDiagnostics().flowerCount),1,'finish prevents further planting');
    await page.reload();
    await page.waitForFunction(() => window.meadowDiagnostics?.().ready);
    assert.equal(await page.evaluate(() => meadowDiagnostics().flowerCount),1,'reload preserves flower');
  } finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }
});
