import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
import {chromium} from 'playwright';

const theme = new URL('../themes/pond-garden/', import.meta.url);
test('desktop seed path, feedback, pause, and reload persistence', async () => {
  const server = createServer(async (req, res) => {
    const name = new URL(req.url, 'http://localhost').pathname.slice(1) || 'flower-meadow.html';
    if (!['flower-meadow.html','pond-botanical.webp','pond-botanical-atlas.webp','pond-workbench.webp'].includes(name)) { res.writeHead(404).end(); return; }
    res.setHeader('Content-Type', name.endsWith('.html') ? 'text/html; charset=utf-8' : 'image/webp');
    res.end(await readFile(new URL(name, theme)));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch();
    const page = await browser.newPage({viewport: {width:1512, height:982}});
    // The seed path must run in daylight so night fireflies cannot consume planting clicks.
    await page.clock.setFixedTime(new Date(2026, 10, 15, 12));
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    await page.waitForFunction(() => window.meadowDiagnostics?.().ready);
    await page.evaluate(() => {
      meadowDesktop('wallpaper',1);
      meadowDesktop('windowsDown',190,690); meadowDesktop('up');
    });
    assert.equal(await page.evaluate(() => meadowDiagnostics().flowerCount),0,'idle desktop must not plant');
    const desktopClick = async (selector,action='windowsDown') => page.evaluate(({selector,action}) => {
      const r = document.querySelector(selector).getBoundingClientRect();
      meadowDesktop(action,r.x+r.width/2,r.y+r.height/2); meadowDesktop('up');
    }, {selector,action});
    await desktopClick('#basket','hover');
    assert.equal(await page.locator('#tool-tip').innerText(),'打开种子架');
    await desktopClick('#basket');
    assert.equal(await page.locator('#seeds-panel').isVisible(),true);
    await desktopClick('#basket');
    assert.equal(await page.locator('#seeds-panel').isVisible(),false);
    await desktopClick('#stump-top','plainDown');
    assert.equal(await page.locator('#seeds-panel').isVisible(),true,'the exposed workbench top opens the same seed rack');
    assert.equal(await page.locator('#rack .seed-art').count(),3,'each offered seed has a botanical illustration');
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
    const legacySave = await page.evaluate(() => {
      const save = JSON.parse(localStorage.getItem('pond-garden-v2'));
      Object.assign(save.flowers[0],{x:.30,y:.41,at:Date.now()-6*60*60*1000});
      const raw = JSON.stringify(save);
      localStorage.setItem('pond-garden-v2',raw);
      return raw;
    });
    await page.reload();
    await page.waitForFunction(() => meadowDiagnostics().ready);
    const root = await page.evaluate(() => {
      const [{x,y,f,tile,flip}] = gardenLayout();
      return {x,y,id:f.id,tile,flip,onLand:shore(x,y),wasInWater:inWater(f.x,f.y)};
    });
    assert.ok(root.wasInWater&&root.onLand,'legacy flowers rooted in the pond must appear on the bank');
    assert.equal(await page.evaluate(() => localStorage.getItem('pond-garden-v2')),legacySave,'rendering must not rewrite the save');
    const save = () => page.evaluate(() => JSON.parse(localStorage.getItem('pond-garden-v2')));
    await page.evaluate(() => meadowDesktop('wallpaper',1));
    await desktopClick('#shears','plainDown');
    assert.equal(await page.locator('#shears').getAttribute('aria-pressed'),'true');
    const snipped = await page.evaluate(({x,y})=>new Promise(resolve=>{
      const snip=document.querySelector('#snip');
      let closed=false;
      const watch=new MutationObserver(()=>{
        closed ||= getComputedStyle(snip).display!=='none'&&snip.classList.contains('closed');
        if(closed&&snip.hasAttribute('hidden')){watch.disconnect();resolve(true);}
      });
      watch.observe(snip,{attributes:true});
      meadowDesktop('plainDown',x*innerWidth,y*innerHeight);meadowDesktop('up');
      setTimeout(()=>{watch.disconnect();resolve(false);},900);
    }),root);
    assert.equal(snipped,true,'the scissors are visible while closing and disappear after the cut');
    assert.equal(await page.evaluate(() => meadowDiagnostics().flowerCount),0,'cutting targets the flower at its visible position');
    const cutStem = {id:root.id,kind:1,tile:root.tile,flip:root.flip};
    assert.deepEqual((await save()).held,cutStem,'cut preserves the chosen flower’s appearance');
    assert.equal(await page.locator('#vase span').innerText(),'插入手里的花');
    await desktopClick('#undo','plainDown');
    assert.equal((await save()).held,null);
    assert.deepEqual((await save()).flowers,JSON.parse(legacySave).flowers,'undo cut restores the original root and growth time');
    await page.evaluate(({x,y})=>meadowDesktop('plainDown',x*innerWidth,y*innerHeight),root);
    await page.reload();
    await page.waitForFunction(() => meadowDiagnostics().ready);
    assert.deepEqual((await save()).held,cutStem,'a cut stem survives reload before arranging');
    await page.evaluate(() => meadowDesktop('wallpaper',1));
    await desktopClick('#vase','plainDown');
    assert.deepEqual((await save()).vase,[cutStem],'inserting keeps the exact cut stem');
    assert.equal((await save()).held,null);
    await desktopClick('#undo','plainDown');
    assert.deepEqual((await save()).held,cutStem,'undo insertion returns the stem to hand');
    assert.deepEqual((await save()).vase,[]);
    await desktopClick('#vase','plainDown');
    await page.reload();
    await page.waitForFunction(() => meadowDiagnostics().ready);
    assert.deepEqual((await save()).vase,[cutStem],'the arranged stem survives reload');
    await page.evaluate(() => meadowDesktop('wallpaper',1));
    const bottle = page.locator('#vase');
    const originalBottle = await bottle.boundingBox();
    await page.evaluate(({x,y,width,height})=>{
      x+=width/2;y+=height/2;
      meadowDesktop('plainDown',x,y);
      meadowDesktop('drag',x+240,y-130);meadowDesktop('up');
    },originalBottle);
    assert.ok((await bottle.boundingBox()).x>originalBottle.x+200,'desktop drag moves the vase');
    assert.equal(await page.locator('#vase-actions').isVisible(),false,'drag is distinct from click');
    await desktopClick('#vase','plainDown');
    await desktopClick('#vase-larger','plainDown');
    await desktopClick('#vase-center','plainDown');
    const centered = await bottle.boundingBox();
    assert.ok(centered.width>originalBottle.width);
    assert.ok(Math.abs(centered.x+centered.width/2-756)<2);
    await page.reload();
    await page.waitForFunction(() => meadowDiagnostics().ready);
    await page.evaluate(() => meadowDesktop('wallpaper',1));
    assert.deepEqual(await bottle.boundingBox(),centered,'position and size survive reload');
    assert.deepEqual((await save()).vase,[cutStem],'moving the vase preserves its contents');
    await desktopClick('#vase','plainDown');
    await desktopClick('#vase-reset','plainDown');
    assert.deepEqual(await bottle.boundingBox(),originalBottle);
    await desktopClick('#vase-done','plainDown');
    await desktopClick('#basket');
    await desktopClick('#rack button:nth-child(2)');
    await page.evaluate(() => {
      for (const [x,y] of [[.23,.45],[.58,.76],[.37,.38]]) {
        meadowDesktop('windowsDown',x*innerWidth,y*innerHeight); meadowDesktop('up');
      }
    });
    const newRoots = await page.evaluate(() => JSON.parse(localStorage.getItem('pond-garden-v2')).flowers.map(f=>[f.x.toFixed(2),f.y.toFixed(2)]));
    assert.deepEqual(newRoots,[['0.23','0.45'],['0.58','0.76']],'both reclaimed banks accept seed while the nearby pond rejects it');
    const beforeDrag=(await save()).flowers;
    await page.evaluate(()=>{
      meadowDesktop('windowsDown',190,690);
      meadowDesktop('drag',470,690);meadowDesktop('up');
    });
    assert.ok((await save()).flowers.length>beforeDrag.length+1);
    await desktopClick('#undo','plainDown');
    assert.deepEqual((await save()).flowers,beforeDrag,'undo removes the entire drag in one step');
    assert.equal(await page.locator('#undo').isVisible(),false);
    await desktopClick('#shears','plainDown');
    const wildStem=await page.evaluate(()=>{
      const noon=new Date();noon.setHours(12,0,0,0);
      Date.now=()=>noon.getTime();draw(performance.now());
      const bloom=wildBlooms.find(p=>p.f.kind>=3&&p.x>innerWidth*.68&&p.y>innerHeight*.55&&document.elementFromPoint(p.x,p.y)===field);
      meadowDesktop('plainDown',bloom.x,bloom.y);meadowDesktop('up');
      return JSON.parse(localStorage.getItem('pond-garden-v2')).held;
    });
    assert.ok(wildStem?.kind>=3,'a visible decorative flower can be clipped');
    assert.deepEqual((await save()).cutWild,[Number(wildStem.id.slice(5))]);
    await desktopClick('#undo','plainDown');
    assert.deepEqual((await save()).cutWild,[],'undo puts the wild flower back');
    assert.equal((await save()).held,null);
    await page.evaluate(id=>{
      draw(performance.now());
      const p=wildBlooms.find(p=>'wild-'+p.f.id===id);
      meadowDesktop('plainDown',p.x,p.y);meadowDesktop('up');
    },wildStem.id);
    await desktopClick('#vase','plainDown');
    await page.reload();
    await page.waitForFunction(()=>meadowDiagnostics().ready);
    assert.deepEqual((await save()).vase.at(-1),wildStem,'a wild stem survives reload');
    await page.evaluate(() => localStorage.setItem('pond-garden-v2','broken prior garden'));
    await page.reload();
    await page.waitForFunction(() => window.meadowDiagnostics?.().ready);
    assert.equal(await page.evaluate(() => meadowDesktop('flush')),false,'unreadable garden prevents a successful update flush');
    assert.equal(await page.evaluate(() => localStorage.getItem('pond-garden-v2')),'broken prior garden','unreadable save must not be overwritten');
    await page.evaluate(() => localStorage.setItem('pond-garden-v2','{"flowers":"damaged"}'));
    await page.reload();
    await page.waitForFunction(() => window.meadowDiagnostics?.().ready);
    assert.equal(await page.evaluate(() => meadowDesktop('flush')),false,'invalid save structure prevents replacement');
    assert.equal(await page.evaluate(() => localStorage.getItem('pond-garden-v2')),'{"flowers":"damaged"}');
    const damagedFlower=JSON.stringify({flowers:[{id:'prior-flower',kind:1,x:.2,y:.7,at:1,duration:'damaged'}],vase:[],held:null});
    await page.evaluate(raw=>localStorage.setItem('pond-garden-v2',raw),damagedFlower);
    await page.reload();
    await page.waitForFunction(() => window.meadowDiagnostics?.().ready);
    assert.equal(await page.evaluate(() => meadowDesktop('flush')),false,'invalid flower must not silently disappear from the saved garden');
    assert.equal(await page.evaluate(() => localStorage.getItem('pond-garden-v2')),damagedFlower);
    await page.route('**/pond-botanical-atlas.webp',route=>route.fulfill({body:'invalid image',contentType:'image/webp'}));
    await page.reload();
    await page.waitForFunction(() => window.meadowDiagnostics?.().frameCount>0);
    assert.equal(await page.evaluate(() => meadowDiagnostics().ready),false,'undecodable theme image must prevent activation');
  } finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }
});
