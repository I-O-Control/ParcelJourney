"""Static geometry QA previews using the tested screen-space positions.
These are diagram renders, not screenshots of the browser application.
"""
from pathlib import Path
import json,textwrap
from PIL import Image,ImageDraw,ImageFont
ROOT=Path(__file__).resolve().parents[1]
data=json.loads((ROOT/'analysis/rotation-layout-samples.json').read_text(encoding='utf-8'))
font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',13)
heading=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',20)
def render(s):
    im=Image.new('RGB',(s['width'],s['height']),'white');d=ImageDraw.Draw(im)
    for ps in s['pipes']:
        d.line([tuple(p) for p in ps],fill='#a5bdcf',width=9)
        d.line([tuple(p) for p in ps],fill='#eaf2f8',width=5)
    for ps in s.get('route',[]):d.line([tuple(p) for p in ps],fill='#0078d4',width=5)
    for n in s['items']:
        x,y=n['x'],n['y'];d.rounded_rectangle((x-7,y-7,x+7,y+7),radius=2,fill='#f1f5fa',outline='#558baa',width=2)
    for b in s.get('boxes',[]):
        x,y,w,h=b['x'],b['y'],b['w'],b['h'];d.line([tuple(b['anchor']),(x+w/2,y+h/2)],fill='#abb7c5')
        d.rounded_rectangle((x,y,x+w,y+h),radius=4,fill='#f4f7fb')
        lines=[];line=''
        for word in b['label'].split():
            candidate=(line+' '+word).strip()
            if font.getlength(candidate)>w-14 and line:lines.append(line);line=word
            else:line=candidate
        if line:lines.append(line)
        assert len(lines)*18<=h,f"Clipped label: {b['label']}"
        for i,line in enumerate(lines):d.text((x+7,y+3+i*18),line,font=font,fill='#172d46')
    if 'parcel' in s:
        x,y=s['parcel'];d.rounded_rectangle((x-11,y-9,x+11,y+9),radius=3,fill='#0078d4')
    return im
render(data['overview']).save(ROOT/'analysis/rotated-plant-overview.png')
sheet=Image.new('RGB',(1800,1290),'#eaf0f6');d=ImageDraw.Draw(sheet)
for i,index in enumerate([0,8,16,24,32,40]):
    s=data['snapshots'][index];im=render(s).resize((600,400))
    x=(i%3)*600;y=(i//3)*645
    d.text((x+12,y+10),f"{s['angle']}° · {s['id']} · {s['time']:.1f}s",font=heading,fill='#172d46')
    sheet.paste(im,(x,y+48))
    d.text((x+12,y+460),'Static layout sample · upright labels / route clearance',font=font,fill='#496079')
sheet.save(ROOT/'analysis/rotation-layout-contact-sheet.png')
print('Rendered plant overview and six-angle diagnostic contact sheet; sampled labels fit measured text.')
