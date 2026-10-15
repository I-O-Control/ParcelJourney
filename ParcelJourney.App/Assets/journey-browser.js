(() => {
  const select = document.getElementById('scenario') || document.getElementById('pick');
  if (!select || document.getElementById('journey-folder-browser')) return;
  const box = document.createElement('section');
  box.id = 'journey-folder-browser'; box.className = 'journey-folder-browser';
  box.setAttribute('aria-label', 'Journey folder and ParcelID search');
  box.innerHTML = `<label for="journey-folder">Journey folder (optional)</label>
    <input id="journey-folder" type="text" placeholder="No folder selected" spellcheck="false">
    <div class="journey-actions"><button id="journey-browse" type="button">Browse…</button><button id="journey-use-folder" type="button">Use folder</button></div>
    <label class="journey-subfolders"><input id="journey-subfolders" type="checkbox" checked> Include subfolders</label>
    <div class="journey-actions"><button id="journey-load-all" type="button" disabled>Load Journeys</button><button id="journey-clear" type="button" hidden>Clear loaded</button></div>
    <label for="journey-search">Search ParcelID in folder</label>
    <div class="journey-search-wrap"><input id="journey-search" type="search" autocomplete="off" spellcheck="false" placeholder="Type at least 4 characters" role="combobox" aria-autocomplete="list" aria-expanded="false" aria-controls="journey-suggestions">
    <div id="journey-suggestions" role="listbox" hidden></div></div>
    <div class="journey-actions"><button id="journey-find" type="button" disabled>Find ParcelID</button><button id="journey-load-one" type="button" disabled>Load selected</button></div>
    <p id="journey-folder-status" role="status" aria-live="polite">No journeys loaded. Folder selection is optional.</p>`;
  select.before(box);
  for(const id of ['search','find','search-form','search-status'])document.getElementById(id)?.setAttribute('hidden','');
  const legacySearch=document.getElementById('search-form')||document.querySelector('.search-form');
  if(legacySearch)legacySearch.style.display='none';
  const legacyScenarioLabel=document.querySelector(`label[for="${select.id}"]`);
  const hideEmptyScenario=()=>{const empty=select.options.length===0;select.style.display=empty?'none':'';if(legacyScenarioLabel)legacyScenarioLabel.style.display=empty?'none':'';document.body.classList.toggle('no-journeys',empty);document.body.classList.toggle('layout-only',location.pathname.startsWith('/3d')&&empty);};
  hideEmptyScenario();
  // The 3D module populates its selector after fetching the replay model.
  // Recheck when options arrive, not only during folder-browser startup.
  new MutationObserver(hideEmptyScenario).observe(select, {childList:true,subtree:true});
  const $ = id => box.querySelector('#' + id);
  const folder=$('journey-folder'), search=$('journey-search'), list=$('journey-suggestions'), status=$('journey-folder-status');
  const recursion=$('journey-subfolders'), loadAll=$('journey-load-all'), loadOne=$('journey-load-one'), find=$('journey-find');
  let choices=[], active=-1, serial=0, timer, selectedId=null, available=false, busy=false;
  const session = fetch('/api/session').then(r=>r.json());
  async function request(url, data) {
    const options = data === undefined ? {} : {method:'POST',headers:{'Content-Type':'application/json','X-ParcelJourney-Session':(await session).token},body:JSON.stringify(data)};
    const response=await fetch(url,options); if(response.status===204)return null;
    const result=await response.json();if(!response.ok)throw Error(result.error || 'Unable to read the selected folder.');return result;
  }
  function dismiss(){list.hidden=true;active=-1;search.setAttribute('aria-expanded','false');search.removeAttribute('aria-activedescendant');}
  function highlight(index){active=index;[...list.children].forEach((row,i)=>row.setAttribute('aria-selected',String(i===index)));if(index>=0){search.setAttribute('aria-activedescendant',list.children[index].id);list.children[index].scrollIntoView({block:'nearest'});}}
  function disableLoading(value){busy=value;loadAll.disabled=value||!available;loadOne.disabled=value||!selectedId;$('journey-browse').disabled=value;$('journey-use-folder').disabled=value;recursion.disabled=value;}
  async function check(id){
    clearTimeout(timer);dismiss(); selectedId=null;loadOne.disabled=true;
    if(!available){status.textContent='Choose an available folder to search. The viewer remains usable without one.';return;}
    const query=++serial;status.textContent='Searching folder catalogue…';
    try{const result=await request('/api/journeys/find?parcelId='+encodeURIComponent(id));if(query!==serial)return;
      selectedId=result.loadable>0?id:null;loadOne.disabled=busy||!selectedId;
      status.textContent=result.found?`${id}: ${result.files} journey file(s) found. ${result.loadable?'Press Load selected to open them.':'Matching index/dictionary missing; events have not been loaded.'}`:`${id}: no match in the selected folder.`;
    }catch(e){if(query===serial)status.textContent=e.message;}
  }
  function choose(id){search.value=id;check(id);search.focus();}
  async function suggest(){
    const prefix=search.value.trim(),query=++serial;dismiss();if(prefix.length<4||!available)return;
    try {const matches=await request('/api/journeys/suggest?prefix='+encodeURIComponent(prefix));if(query!==serial||prefix!==search.value.trim())return;
      choices=matches;list.replaceChildren();choices.forEach((item,i)=>{const button=document.createElement('button');button.type='button';button.id='journey-option-'+i;button.setAttribute('role','option');button.setAttribute('aria-selected','false');button.textContent=`${item.parcelId} · ${item.files} file(s)`;button.addEventListener('click',()=>choose(item.parcelId));list.append(button);});
      list.hidden=choices.length===0;search.setAttribute('aria-expanded',String(choices.length>0));status.textContent=choices.length?`${choices.length} matching ParcelIDs. Use ↑/↓ and Enter, or click a result.`:'No prefix matches in this folder.';
    }catch(e){if(query===serial)status.textContent=e.message;}
  }
  search.addEventListener('input',()=>{serial++;selectedId=null;loadOne.disabled=true;find.disabled=!search.value.trim()||!available;dismiss();clearTimeout(timer);timer=setTimeout(suggest,180);});
  search.addEventListener('keydown',e=>{
    if((e.key==='ArrowDown'||e.key==='ArrowUp')&&!list.hidden&&choices.length){e.preventDefault();highlight(active<0?(e.key==='ArrowDown'?0:choices.length-1):(active+(e.key==='ArrowDown'?1:-1)+choices.length)%choices.length);}
    else if(e.key==='Enter'){e.preventDefault();clearTimeout(timer);if(!list.hidden&&active>=0)choose(choices[active].parcelId);else if(search.value.trim())check(search.value.trim());}
    else if(e.key==='Escape'){dismiss();serial++;clearTimeout(timer);}
  });
  document.addEventListener('pointerdown',e=>{if(!box.contains(e.target))dismiss();});
  find.onclick=()=>check(search.value.trim());
  async function save(path){await request('/api/journeys/preferences',{folder:path || null,includeSubfolders:recursion.checked});location.reload();}
  $('journey-use-folder').onclick=async()=>{try{await save(folder.value.trim());}catch(e){status.textContent=e.message;}};
  $('journey-browse').onclick=async()=>{try{status.textContent='Choose a folder…';const result=await request('/api/journeys/pick-folder',{});if(result)await save(result.path);else status.textContent='Folder selection cancelled. No journeys were loaded.';}catch(e){status.textContent=e.message;}};
  recursion.onchange=async()=>{try{await save(folder.value.trim());}catch(e){status.textContent=e.message;}};
  async function load(id){disableLoading(true);dismiss();status.textContent=id?`Loading ${id}…`:'Loading published journeys from the selected folder…';
    try{const report=await request('/api/journeys/load',{parcelId:id || null});sessionStorage.setItem('parceljourney-load-result',`${report.runs} journey(s), ${report.events} events loaded${report.unindexedFilesSkipped?`; ${report.unindexedFilesSkipped} unindexed file(s) skipped`:''}.`);location.reload();}
    catch(e){status.textContent=e.message;disableLoading(false);}
  }
  loadAll.onclick=()=>load(null);loadOne.onclick=()=>{if(selectedId)load(selectedId);};
  $('journey-clear').onclick=async()=>{try{await request('/api/journeys/clear',{});sessionStorage.removeItem('parceljourney-load-result');location.reload();}catch(e){status.textContent=e.message;}};
  request('/api/journeys/preferences').then(async preferences=>{
    hideEmptyScenario();
    folder.value=preferences.folder || '';recursion.checked=preferences.includeSubfolders;available=preferences.available;
    search.disabled=!available;find.disabled=true;loadAll.disabled=!available;$('journey-clear').hidden=preferences.loadedJourneys===0;
    if(!preferences.folder){status.textContent='No folder selected. Explore the layout, or choose a journey folder.';return;}
    if(!available){status.textContent='The remembered folder is unavailable. Choose another folder; the viewer still works.';return;}
    status.textContent='Reading the file catalogue; journeys are not loaded automatically…';
    try{const catalogue=await request('/api/journeys/catalogue');loadAll.disabled=catalogue.loadable===0;const message=sessionStorage.getItem('parceljourney-load-result');sessionStorage.removeItem('parceljourney-load-result');status.textContent=preferences.loadedJourneys>0?(message||`${preferences.loadedJourneys} journey(s) loaded.`):`${catalogue.parcels} ParcelIDs in ${catalogue.files} file(s). No journeys loaded. Search one, or press Load Journeys.`;}catch(e){status.textContent=e.message;}
  }).catch(e=>{status.textContent=e.message;});
  const editor=document.createElement('a');editor.href='/layout-editor'+(location.pathname.startsWith('/3d')?'?return=3d':'');editor.textContent='Edit layout';editor.style.cssText='margin-left:12px;color:inherit';document.querySelector('header')?.append(editor);
})();
