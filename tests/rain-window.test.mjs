import {test} from 'node:test';
import assert from 'node:assert/strict';
import {chromium} from 'playwright';

for(const pink of [false,true]) test((pink?'pink':'blue')+' rain draws, responds to desktop input and pauses', async () => {
  const browser = await chromium.launch();
  try {
    const page = await browser.newPage();
    await page.goto(new URL('../themes/rain-window/rain-window.html'+(pink?'?pink=1':''), import.meta.url).href);
    await page.waitForFunction(() => meadowDiagnostics().frameCount > 2);
    assert.equal(await page.title(),pink?'粉暮窗台':'雨夜窗台');
    assert.equal(await page.evaluate(() => meadowDesktop('flush')), true);
    await page.evaluate(() => meadowDesktop('wallpaper', true));
    assert.equal(await page.evaluate(() => meadowDesktop('windowsDown', innerWidth/2, innerHeight/2)), true);
    await page.evaluate(() => meadowDesktop('up'));
    assert.equal(await page.evaluate(() => meadowDiagnostics().splashCount), 1);
    await page.evaluate(() => meadowDesktop('visible', false));
    const frame = await page.evaluate(() => meadowDiagnostics().frameCount);
    await page.waitForTimeout(150);
    assert.equal(await page.evaluate(() => meadowDiagnostics().frameCount), frame);
    await page.evaluate(() => meadowDesktop('visible', true));
    await page.waitForFunction(n => meadowDiagnostics().frameCount > n, frame);
    await page.emulateMedia({reducedMotion:'reduce'});
    await page.waitForTimeout(100);
    assert.equal(await page.evaluate(() => meadowDesktop('windowsDown', innerWidth/2, innerHeight/2)), false);
  } finally { await browser.close(); }
});

test('pink glass drag clears the fog, then the fog returns', async () => {
  const browser = await chromium.launch();
  try {
    const page = await browser.newPage({viewport:{width:960,height:540}});
    await page.goto(new URL('../themes/rain-window/rain-window.html?pink=1', import.meta.url).href);
    await page.waitForFunction(() => meadowDiagnostics().ready);
    const alpha = () => {
      const c=document.querySelector('#fog');
      return c.getContext('2d').getImageData(480,250,1,1).data[3];
    };
    const before=await page.evaluate(alpha);
    assert.ok(before>0);
    await page.mouse.move(405,250);
    await page.mouse.down();
    await page.mouse.move(560,250,{steps:12});
    await page.mouse.up();
    await page.waitForFunction(alpha=>document.querySelector('#fog').getContext('2d').getImageData(480,250,1,1).data[3]<alpha/2,before);
    assert.equal(await page.evaluate(() => meadowDiagnostics().splashCount),0);
    await page.waitForFunction(() => meadowDiagnostics().wipeCount===0,undefined,{timeout:7000});
    assert.equal(await page.evaluate(alpha),before);
    await page.mouse.click(480,250,{button:'right'});
    await page.mouse.move(480,500);
    await page.mouse.down();
    await page.mouse.move(480,250,{steps:8});
    await page.mouse.up();
    assert.deepEqual(await page.evaluate(() => {
      const {splashCount,wipeCount}=meadowDiagnostics();
      return [splashCount,wipeCount];
    }),[0,0]);
  } finally { await browser.close(); }
});
