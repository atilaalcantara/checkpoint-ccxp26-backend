// Public reproduction. Requires `playwright` installed in the caller's environment.
// RUN_HLG_VALIDATION=yes node reproduce.mjs
// Creates TWO NEW HLG credentials, three polls, three participating visitors/photos.
// Browser authenticator and private keys live only in memory; no storageState/trace/HAR.
import { chromium } from 'playwright';
import assert from 'node:assert/strict';
if (process.env.RUN_HLG_VALIDATION !== 'yes') throw new Error('Set RUN_HLG_VALIDATION=yes to create HLG test data');
const HLG = 'https://ccxp26-hlg.atilaalcantara.com';
const PRD = 'https://ccxp26.atilaalcantara.com';
const browser = await chromium.launch({headless:true});
const context = await browser.newContext();
const page = await context.newPage();
const report = [];
const check = (test, actual, expected) => report.push({test,actual,expected,pass:actual===expected});
try {
  await page.goto(HLG+'/admin/');
  const cdp = await context.newCDPSession(page);
  await cdp.send('WebAuthn.enable',{enableUI:false});
  await cdp.send('WebAuthn.addVirtualAuthenticator',{options:{protocol:'ctap2',transport:'internal',hasResidentKey:true,hasUserVerification:true,isUserVerified:true,automaticPresenceSimulation:true}});
  for(let i=0;i<2;i++) {
    const response=page.waitForResponse(r=>r.url()===HLG+'/api/admin/passkeys/register/verify');
    await page.getByRole('button',{name:'Cadastrar Passkey',exact:true}).click();
    check(`register ${i+1}`,(await response).status(),201);
    check(`register button after ${i+1}`,await page.getByRole('button',{name:'Cadastrar Passkey',exact:true}).isVisible(),true);
  }
  await page.getByRole('button',{name:'Entrar com Passkey',exact:true}).click();
  await page.getByRole('button',{name:'Sair',exact:true}).waitFor();
  const cookie=(await context.cookies(HLG)).find(c=>c.name==='checkpoint.admin');
  assert(cookie);
  const isolated=await context.request.get(PRD+'/api/admin/auth/me',{headers:{Cookie:`checkpoint.admin=${cookie.value}`}});
  check('HLG cookie rejected in PRD',isolated.status(),401);
  for(const method of ['GET','POST']) for(const endpoint of ['options','verify']) {
    const response=await context.request.fetch(PRD+'/api/admin/passkeys/register/'+endpoint,{method,...(method==='POST'?{data:{}}:{})});
    // Preserved private/invite-gated API: existence is not public enrollment.
    const expected=method==='GET'?405:endpoint==='options'?401:400;
    check(`PRD registration denied ${method} ${endpoint}`,response.status(),expected);
  }
  // Empty assertion-options request only: never complete a PRD challenge.
  check('PRD login options',(await context.request.post(PRD+'/api/admin/auth/passkey/options',{data:{}})).status(),200);
  const response=page.waitForResponse(r=>r.url()===HLG+'/api/admin/auth/logout');
  await page.getByRole('button',{name:'Sair',exact:true}).click();
  check('logout',(await response).status(),204);
  check('logged out me',(await context.request.get(HLG+'/api/admin/auth/me')).status(),401);
  await page.getByRole('button',{name:'Entrar com Passkey',exact:true}).click();
  await page.getByRole('button',{name:'Sair',exact:true}).waitFor();
  check('logged in me',(await context.request.get(HLG+'/api/admin/auth/me')).status(),200);
  // All remaining mutations are guarded in the browser against PRD.
  const results=await page.evaluate(async(HLG)=>{
    if(location.origin!==HLG)throw new Error('HLG only');
    const req=async(path,init={})=>{const r=await fetch(path,init);const text=await r.text();return {status:r.status,body:text?JSON.parse(text):null};};
    const token=(await req('/api/admin/auth/csrf')).body.token;
    const headers={'Content-Type':'application/json','X-CSRF-Token':token};
    const noCsrf=await req('/api/admin/site-status',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({status:'live'})});
    for(let i=1;i<=3;i++)await req('/api/admin/polls',{method:'POST',headers,body:JSON.stringify({question:`HLG teste ${i}: qual aventura?`,a:'Magia',b:'Tecnologia',active:true})});
    await req('/api/admin/site-status',{method:'PUT',headers,body:JSON.stringify({status:'live'})});
    const initial=(await req('/api/v1/progress')).body.totalXp;
    const canvas=document.createElement('canvas');canvas.width=720;canvas.height=660;
    const ctx=canvas.getContext('2d');ctx.fillStyle='#334155';ctx.fillRect(0,0,720,660);ctx.fillStyle='#ffcf42';ctx.font='48px sans-serif';ctx.fillText('HLG validation',100,300);
    const blob=await new Promise(r=>canvas.toBlob(r,'image/png'));
    const runs=[];
    for(let i=0;i<3;i++){
      const h={'X-Visitor-Id':crypto.randomUUID()};
      const participation=(await req('/api/v1/participation',{method:'PUT',headers:h})).body;
      const locked=await req('/api/v1/results',{headers:h});
      const votes=[];
      for(const p of participation.polls) votes.push(await req('/api/v1/votes',{method:'POST',headers:{...h,'Content-Type':'application/json'},body:JSON.stringify({pollId:p.id,optionId:p.options[0].id})}));
      const f=new FormData();f.append('file',blob,'hlg-validation.png');
      const photo=await req('/api/v1/photos',{method:'POST',headers:h,body:f});
      const unlocked=await req('/api/v1/results',{headers:h});
      runs.push({locked:locked.status,votes:votes.map(v=>({status:v.status,xp:v.body.awardedXp})),photo:{status:photo.status,xp:photo.body.awardedXp},results:unlocked.status});
    }
    const progress=(await req('/api/v1/progress')).body;
    const recent=(await req('/api/v1/photos/recent')).body;
    return {csrfDenied:noCsrf.status,xpDelta:progress.totalXp-initial,runs,level:progress.currentLevel.id,thumbnails:recent.photos.map(p=>({type:p.image.mediaType,width:p.image.width,height:p.image.height}))};
  },HLG);
  check('CSRF missing denied',results.csrfDenied,403);
  check('3 full participants +15 XP',results.xpDelta,15);
  report.push({test:'HLG API runs',details:results});
  console.log(JSON.stringify(report,null,2));
  if(report.some(r=>r.pass===false))process.exitCode=1;
} finally { await browser.close(); }
