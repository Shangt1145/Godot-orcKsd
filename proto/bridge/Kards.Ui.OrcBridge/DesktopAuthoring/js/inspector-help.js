/* The tutorial stays in this page, preserving the current editor and draft. */
(function(){
  'use strict';
  const launch=document.querySelector('[data-inspector-tutorial]');
  if(!launch)return;
  let dialog,frame,previous,closeGuard;
  function close(){if(dialog?.open){dialog.close();if(previous?.isConnected)previous.focus({preventScroll:true});}}
  launch.addEventListener('click',()=>{
    previous=document.activeElement;
    if(!dialog){
      dialog=document.createElement('dialog');dialog.className='ins-tutorial-dialog';dialog.setAttribute('aria-label','Inspector 新手教程');
      const head=document.createElement('div');head.className='ins-tutorial-heading';
      const title=document.createElement('strong');title.textContent='Inspector 新手教程';
      const exit=document.createElement('button');exit.type='button';exit.textContent='关闭教程';exit.addEventListener('click',close);
      head.append(title,exit);
      frame=document.createElement('iframe');frame.title='Inspector 新手教程正文';frame.src='./inspector-tutorial.html';frame.setAttribute('sandbox','allow-scripts');
      dialog.append(head,frame);document.body.append(dialog);
      dialog.addEventListener('click',event=>{if(event.target===dialog){const r=dialog.getBoundingClientRect();if(event.clientX<r.left||event.clientX>r.right||event.clientY<r.top||event.clientY>r.bottom)close();}});
      dialog.addEventListener('cancel',event=>{event.preventDefault();close();});
      window.addEventListener('message',event=>{if(event.source===frame.contentWindow&&event.data?.type==='inspector-tutorial-close')close();});
    }
    if(typeof window.KG_BEFORE_CLOSE==='function'&&window.KG_BEFORE_CLOSE!==closeGuard){const guard=window.KG_BEFORE_CLOSE;closeGuard=async(...args)=>{close();return guard(...args);};window.KG_BEFORE_CLOSE=closeGuard;}
    dialog.showModal();dialog.querySelector('button').focus();
  });
})();
