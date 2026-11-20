const $=id=>document.getElementById(id);
let contacts=[];
let pollTimer=null;
let setupMode=false;
let showArchived=false;

async function req(path,options={}){
  const r=await fetch(path,{credentials:'include',...options,headers:{...(options.body instanceof FormData?{}:{'Content-Type':'application/json'}),...(options.headers||{})}});
  if(r.status===401){showLogin();throw new Error('Невірний логін або пароль');}
  if(!r.ok){let t=await r.text();try{t=JSON.parse(t).error||t}catch{}throw new Error(t||`HTTP ${r.status}`)}
  const ct=r.headers.get('content-type')||'';
  return ct.includes('application/json')?r.json():r;
}
function showLogin(){$('loginView').classList.remove('hidden');$('appView').classList.add('hidden');if(pollTimer){clearInterval(pollTimer);pollTimer=null}}
function showApp(){$('loginView').classList.add('hidden');$('appView').classList.remove('hidden');refreshAll();if(!pollTimer)pollTimer=setInterval(refreshAll,10000)}
function toast(text){const t=$('toast');t.textContent=text;t.classList.remove('hidden');setTimeout(()=>t.classList.add('hidden'),4200)}
function esc(s=''){return String(s).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]))}
function statusPill(el,state){const ok=state?.connected;el.textContent=ok?'Підключено':state?.status||'Не підключено';el.className='status-pill '+(ok?'ok':(state?.status||'').startsWith('error')?'bad':'warn')}

async function login(){try{const body={username:$('loginUser').value,password:$('loginPass').value};if(setupMode){await req('webapi/setup',{method:'POST',body:JSON.stringify({setupCode:$('setupCode').value,username:body.username,password:body.password})});setupMode=false;$('setupCodeWrap').classList.add('hidden');$('loginBtn').textContent='Увійти';$('loginError').textContent='Доступ створено. Входимо…'}await req('webapi/login',{method:'POST',body:JSON.stringify(body)});$('loginError').textContent='';showApp()}catch(e){$('loginError').textContent=e.message}}
async function logout(){try{await req('webapi/logout',{method:'POST',body:'{}'})}catch{}showLogin()}
async function refreshAll(){try{await Promise.all([loadStatus(),loadContacts(),loadSummary(),loadMessages()])}catch(e){console.warn(e)}}
async function loadStatus(){const s=await req('webapi/status');statusPill($('tgState'),s.telegram);statusPill($('waState'),s.whatsapp);$('tgAccount').textContent=s.telegram?.account||'';$('waAccount').textContent=s.whatsapp?.account||''}

async function loadContacts(){contacts=await req(`webapi/contacts?includeArchived=${showArchived?'true':'false'}`);renderContacts()}
function telegramMark(c){const d=(c.telegramDeliveryState||'').toLowerCase();if(d==='read'||d==='replied')return '<span class="channel-mark read" title="Telegram: повідомлення прочитано">✓</span>';if(d==='sent')return '<span class="channel-mark sent" title="Telegram: повідомлення відправлено">✓</span>';if(d==='failed')return '<span class="channel-mark failed" title="Telegram: повідомлення не відправлено">×</span>';if(d==='queued'||d==='processing')return '<span class="channel-mark idle" title="Telegram: повідомлення в черзі">…</span>';return '<span class="channel-mark idle" title="Telegram: ще не відправляли">—</span>'}
function whatsappMark(c){const d=(c.whatsAppDeliveryState||'').toLowerCase();if(d==='read'||d==='replied')return '<span class="channel-mark read" title="WhatsApp: повідомлення прочитано">✓</span>';if(d==='sent')return '<span class="channel-mark sent" title="WhatsApp: повідомлення відправлено">✓</span>';const s=(c.whatsAppState||'unknown').toLowerCase();if(s==='yes')return '<span class="channel-mark available" title="WhatsApp: акаунт є">+</span>';if(s==='no')return '<span class="channel-mark absent" title="WhatsApp: акаунта немає">−</span>';return '<span class="channel-mark idle" title="WhatsApp: ще не перевірено">—</span>'}
function renderContacts(){
  const q=$('searchBox').value.trim().toLowerCase();
  const selected=new Set([...document.querySelectorAll('.contact-check:checked')].map(x=>Number(x.value)));
  const rows=contacts.filter(c=>!q||c.name.toLowerCase().includes(q)||c.phone.includes(q)||(c.notes||'').toLowerCase().includes(q));
  $('contactsBody').innerHTML=rows.map(c=>{
    const actions=c.isArchived
      ? `<button class="small-btn restore" data-id="${c.id}">Відновити</button> <button class="small-btn delete-contact" data-id="${c.id}">Видалити</button>`
      : `<button class="small-btn archive" data-id="${c.id}">Архів</button> <button class="small-btn delete-contact" data-id="${c.id}">Видалити</button>`;
    return `<tr${c.isArchived?' style="opacity:.65"':''}><td><input class="contact-check" type="checkbox" value="${c.id}" ${selected.has(c.id)?'checked':''}></td><td>${esc(c.name)}${c.isArchived?' <span class="muted">(архів)</span>':''}</td><td>${esc(c.phone)}</td><td>${telegramMark(c)}</td><td>${whatsappMark(c)}</td><td>${esc(c.notes||'')}</td><td>${actions}</td></tr>`;
  }).join('')||'<tr><td colspan="7" class="muted">Контактів немає</td></tr>';
  document.querySelectorAll('.contact-check').forEach(x=>x.onchange=updateSelected);
  document.querySelectorAll('.archive').forEach(b=>b.onclick=()=>archiveContact(Number(b.dataset.id)));
  document.querySelectorAll('.restore').forEach(b=>b.onclick=()=>restoreContact(Number(b.dataset.id)));
  document.querySelectorAll('.delete-contact').forEach(b=>b.onclick=()=>deleteContact(Number(b.dataset.id)));
  updateSelected();
}
function updateSelected(){$('selectedCount').textContent=document.querySelectorAll('.contact-check:checked').length}
async function archiveContact(id){if(!confirm('Перемістити контакт в архів? Він не видалиться і його можна буде відновити.'))return;await req(`webapi/contacts/${id}/archive`,{method:'POST',body:'{}'});await loadContacts();toast('Контакт переміщено в архів')}
async function restoreContact(id){await req(`webapi/contacts/${id}/restore`,{method:'POST',body:'{}'});await loadContacts();toast('Контакт відновлено')}
async function deleteContact(id){if(!confirm('Видалити контакт з CRM назавжди? Історія повідомлень залишиться, доки її окремо не очистити.'))return;await req(`webapi/contacts/${id}`,{method:'DELETE'});await loadContacts();toast('Контакт видалено')}
async function addContact(){showModal('Новий контакт',`<div class="modal-form"><input id="newName" placeholder="Ім'я"><input id="newPhone" placeholder="+380..."><input id="newNotes" placeholder="Примітка"><button id="saveContact" class="primary">Зберегти</button></div>`);$('saveContact').onclick=async()=>{try{await req('webapi/contacts',{method:'POST',body:JSON.stringify({name:$('newName').value,phone:$('newPhone').value,notes:$('newNotes').value})});hideModal();toast('Контакт додано');try{await loadContacts()}catch{toast('Контакт збережено. Не вдалося оновити список — натисни “Оновити”.')}}catch(e){toast(e.message)}}}
async function importExcel(){const f=$('excelFile').files[0];if(!f){toast('Спочатку вибери Excel-файл');return}const fd=new FormData();fd.append('file',f);try{const r=await req('webapi/contacts/import',{method:'POST',body:fd});toast(`Додано: ${r.added}; дублікати: ${r.duplicates}; помилки: ${r.invalid}`);await loadContacts()}catch(e){toast(e.message)}}
async function checkChannels(){const ids=[...document.querySelectorAll('.contact-check:checked')].map(x=>Number(x.value));if(!ids.length){toast('Вибери контакти для перевірки WhatsApp');return}if(ids.length>25){toast('За один раз можна перевірити до 25 контактів');return}try{$('checkChannelsBtn').disabled=true;$('checkChannelsBtn').textContent='Перевіряю WhatsApp…';const r=await req('webapi/contacts/check-channels',{method:'POST',body:JSON.stringify({contactIds:ids})});toast(`WhatsApp перевірено: ${r.checkedCount}. Є: ${r.whatsapp?.yes??0}; немає: ${r.whatsapp?.no??0}; невідомо: ${r.whatsapp?.unknown??0}`);await loadContacts()}catch(e){toast(e.message)}finally{$('checkChannelsBtn').disabled=false;$('checkChannelsBtn').textContent='Перевірити WhatsApp'}}

function statusUa(s){return ({queued:'в черзі',processing:'відправляється',sent:'надіслано',read:'прочитано',replied:'відповів',failed:'помилка',incoming:'вхідне'})[s]||s}
async function sendSelected(){const selectedIds=[...document.querySelectorAll('.contact-check:checked')].map(x=>Number(x.value));const text=$('messageText').value.trim();const channel=document.querySelector('input[name=channel]:checked').value;if(!selectedIds.length){toast('Вибери хоча б один контакт');return}if(!text){toast('Введи текст повідомлення');return}const selectedContacts=contacts.filter(c=>selectedIds.includes(c.id)&&!c.isArchived);const eligible=channel==='telegram'?selectedContacts:selectedContacts.filter(c=>c.whatsAppState==='yes');const skipped=selectedContacts.length-eligible.length;if(!eligible.length){toast(channel==='telegram'?'Немає вибраних контактів':'Серед вибраних немає підтверджених WhatsApp-акаунтів');return}const question=channel==='telegram'?`Буде зроблено спробу відправити в Telegram: ${eligible.length}. Якщо відправлення не пройде, біля контакту з'явиться червоний ×. Продовжити?`:`Буде відправлено в WhatsApp: ${eligible.length}. Пропущено без підтвердженого WhatsApp: ${skipped}. Продовжити?`;if(!confirm(question))return;try{const r=await req('webapi/messages/bulk',{method:'POST',body:JSON.stringify({contactIds:eligible.map(c=>c.id),channel,text})});toast(`У чергу додано: ${r.queued}`);await Promise.all([loadContacts(),loadSummary(),loadMessages()])}catch(e){toast(e.message)}}
async function loadSummary(){const s=await req('webapi/messages/summary');$('mQueued').textContent=s.queued;$('mSent').textContent=s.sent;$('mRead').textContent=s.read;$('mReplied').textContent=s.replied;$('mFailed').textContent=s.failed}
async function loadMessages(){
  const selected=new Set([...document.querySelectorAll('.message-check:checked')].map(x=>x.value));
  const m=await req('webapi/messages/recent?limit=100');
  $('messagesBody').innerHTML=m.map(x=>`<tr><td><input class="message-check" type="checkbox" value="${esc(x.id)}" ${selected.has(x.id)?'checked':''}></td><td>${new Date(x.createdAtUtc).toLocaleString()}</td><td>${esc(x.contactName||x.phone)}</td><td>${esc(x.channel)}</td><td>→ вихідне</td><td class="status-text status-${esc(x.status)}">${esc(statusUa(x.status))}</td><td class="message-cell">${esc(x.text)}${x.replyText?`<div class="reply-text"><b>Відповідь:</b> ${esc(x.replyText)}</div>`:''}${x.error?`<div class="error">${esc(x.error)}</div>`:''}</td></tr>`).join('')||'<tr><td colspan="7" class="muted">Історії ще немає</td></tr>';
  document.querySelectorAll('.message-check').forEach(x=>x.onchange=()=>{});
  $('messagesSelectAll').checked=false;
}
async function deleteSelectedMessages(){const ids=[...document.querySelectorAll('.message-check:checked')].map(x=>x.value);if(!ids.length){toast('Вибери записи для видалення');return}if(!confirm(`Прибрати вибрані записи з Останньої активності: ${ids.length}?`))return;const r=await req('webapi/messages/delete',{method:'POST',body:JSON.stringify({messageIds:ids})});toast(`Очищено: ${r.deleted}`);await Promise.all([loadMessages(),loadSummary()])}
async function clearMessageHistory(){if(!confirm('Очистити всю завершену історію? Повідомлення, які ще в черзі або зараз відправляються, залишаться.'))return;const r=await req('webapi/messages/clear',{method:'POST',body:'{}'});toast(`Історію очищено. Видалено: ${r.deleted}`);await Promise.all([loadMessages(),loadSummary()])}

async function exportResults(all){
  const ids=[...document.querySelectorAll('.contact-check:checked')].map(x=>Number(x.value));
  if(!all&&!ids.length){toast('Вибери контакти для експорту');return}
  try{
    const r=await fetch('webapi/messages/export',{method:'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({contactIds:all?null:ids})});
    if(r.status===401){showLogin();return}
    if(!r.ok){let t=await r.text();try{t=JSON.parse(t).error||t}catch{}throw new Error(t||`HTTP ${r.status}`)}
    const blob=await r.blob();
    const a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download=`MessengerCRM_results_${new Date().toISOString().slice(0,10)}.xlsx`;document.body.appendChild(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(a.href),2000);toast('Excel сформовано');
  }catch(e){toast(e.message)}
}

function showModal(title,html){$('modalTitle').textContent=title;$('modalBody').innerHTML=html;$('modal').classList.remove('hidden')}
function hideModal(){$('modal').classList.add('hidden');$('modalBody').innerHTML=''}
async function telegramQr(){showModal('Telegram — підключення через TDLib',`<p>На телефоні: <b>Telegram → Налаштування → Пристрої → Підключити пристрій</b>.</p><div id="qrWait" class="muted">Генеруємо QR…</div><img id="tgQrImg" class="qr hidden"><div id="tg2faWrap" class="modal-form hidden" style="margin-top:14px"><input id="tg2fa" type="password" placeholder="Пароль двофакторної автентифікації"><button id="tg2faBtn" class="primary">Підтвердити 2FA</button></div><p class="muted">Якщо після сканування Telegram попросить 2FA, поле з’явиться тут автоматично.</p>`);try{await req('webapi/telegram/qr/start',{method:'POST',body:JSON.stringify({password:null})});await updateTelegramQr()}catch(e){$('qrWait').textContent=e.message}}
async function submitTelegram2fa(){const p=$('tg2fa').value;if(!p){$('qrWait').textContent='Введи пароль 2FA';return}try{$('tg2faBtn').disabled=true;$('qrWait').textContent='Перевіряємо 2FA…';const r=await req('webapi/telegram/password',{method:'POST',body:JSON.stringify({password:p})});if(r.ready){$('qrWait').textContent='Telegram підключено.';$('tg2faWrap').classList.add('hidden');toast('Telegram підключено');await loadStatus();return}$('qrWait').textContent=r.error||'Пароль не прийнято';$('tg2faBtn').disabled=false}catch(e){$('qrWait').textContent=e.message;$('tg2faBtn').disabled=false}}
async function updateTelegramQr(){try{const s=await req('webapi/status');if(s.telegram.connected){$('qrWait').textContent='Telegram підключено.';$('tgQrImg').classList.add('hidden');$('tg2faWrap').classList.add('hidden');toast('Telegram підключено');loadStatus();return}if(s.telegram.status==='password_required'){$('tgQrImg').classList.add('hidden');$('tg2faWrap').classList.remove('hidden');$('qrWait').textContent='QR прийнято. Тепер введи пароль двофакторної автентифікації Telegram.';$('tg2faBtn').onclick=submitTelegram2fa;return}if((s.telegram.status||'').startsWith('qr_error:')){$('qrWait').textContent=s.telegram.status.replace('qr_error:','Помилка Telegram:').trim();return}const r=await fetch('webapi/telegram/qr',{credentials:'include',cache:'no-store'});if(r.ok){const b=await r.blob();$('tgQrImg').src=URL.createObjectURL(b);$('tgQrImg').classList.remove('hidden');$('qrWait').textContent='Скануй QR телефоном.'}else{$('qrWait').textContent='Очікуємо QR…'}}catch(e){$('qrWait').textContent=e.message||'Очікуємо Telegram…'}setTimeout(()=>{if(!$('modal').classList.contains('hidden'))updateTelegramQr()},1500)}
async function whatsappQr(){showModal('WhatsApp — підключення','<p>На телефоні: <b>WhatsApp → Налаштування → Пов’язані пристрої → Пов’язати пристрій</b>.</p><div id="qrWait" class="muted">Запускаємо WhatsApp Web.js…</div><img id="waQrImg" class="qr hidden"><p class="muted">Після сканування сервер збереже сесію.</p>');try{await req('webapi/whatsapp/start',{method:'POST',body:'{}'});await updateWaQr()}catch(e){$('qrWait').textContent=e.message}}
async function updateWaQr(){try{const r=await fetch('webapi/whatsapp/screenshot',{credentials:'include',cache:'no-store'});if(r.ok){const b=await r.blob();$('waQrImg').src=URL.createObjectURL(b);$('waQrImg').classList.remove('hidden');$('qrWait').textContent='Скануй цей QR телефоном.'}}catch{}setTimeout(async()=>{try{const s=await req('webapi/status');if(s.whatsapp.connected){$('qrWait').textContent='WhatsApp підключено.';toast('WhatsApp підключено');loadStatus();return}}catch{}if(!$('modal').classList.contains('hidden'))updateWaQr()},3000)}
function changePassword(){showModal('Змінити пароль',`<div class="modal-form"><input id="curPass" type="password" placeholder="Поточний пароль"><input id="newPass" type="password" placeholder="Новий пароль (мін. 10 символів)"><button id="savePass" class="primary">Змінити пароль</button></div>`);$('savePass').onclick=async()=>{try{await req('webapi/password',{method:'POST',body:JSON.stringify({currentPassword:$('curPass').value,newPassword:$('newPass').value})});hideModal();toast('Пароль змінено')}catch(e){toast(e.message)}}}

$('loginBtn').onclick=login;
$('loginPass').onkeydown=e=>{if(e.key==='Enter')login()};
$('logoutBtn').onclick=logout;
$('refreshBtn').onclick=refreshAll;
$('passwordBtn').onclick=changePassword;
$('searchBox').oninput=renderContacts;
$('importBtn').onclick=importExcel;
$('checkChannelsBtn').onclick=checkChannels;
$('selectVisibleBtn').onclick=()=>{document.querySelectorAll('.contact-check').forEach(x=>x.checked=true);$('selectAll').checked=true;updateSelected()};
$('clearSelectionBtn').onclick=()=>{document.querySelectorAll('.contact-check').forEach(x=>x.checked=false);$('selectAll').checked=false;updateSelected()};
$('showArchivedBtn').onclick=async()=>{showArchived=!showArchived;$('showArchivedBtn').textContent=showArchived?'Тільки активні':'Показати архів';await loadContacts()};
$('exportSelectedBtn').onclick=()=>exportResults(false);
$('exportAllBtn').onclick=()=>exportResults(true);
$('addContactBtn').onclick=addContact;
$('sendBtn').onclick=sendSelected;
$('selectAll').onchange=e=>{document.querySelectorAll('.contact-check').forEach(x=>x.checked=e.target.checked);updateSelected()};
$('deleteMessagesBtn').onclick=deleteSelectedMessages;
$('clearMessagesBtn').onclick=clearMessageHistory;
$('messagesSelectAll').onchange=e=>document.querySelectorAll('.message-check').forEach(x=>x.checked=e.target.checked);
$('tgQrBtn').onclick=telegramQr;
$('waQrBtn').onclick=whatsappQr;
$('modalClose').onclick=hideModal;
$('modal').onclick=e=>{if(e.target===$('modal'))hideModal()};

(async()=>{try{const setup=await req('webapi/setup/status');if(!setup.configured){setupMode=true;$('setupCodeWrap').classList.remove('hidden');$('loginBtn').textContent='Створити доступ';$('loginError').textContent='Перший запуск: введи одноразовий код, придумай логін і пароль (мін. 10 символів).';showLogin();return}await req('webapi/me');showApp()}catch{showLogin()}})();
