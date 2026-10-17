import hashlib,json,pathlib,sys,zipfile,xml.etree.ElementTree as ET
folder=pathlib.Path(sys.argv[1]); ns={'s':'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}; reports=[]
for file in sorted(folder.glob('workbook-*.xlsx')):
 variant=file.stem.removeprefix('workbook-'); events=json.loads((folder/f'observations-{variant}.json').read_text())
 with zipfile.ZipFile(file) as z:
  shared=[]
  if 'xl/sharedStrings.xml' in z.namelist():
   shared=[''.join(t.text or '' for t in e.findall('.//s:t',ns)) for e in ET.fromstring(z.read('xl/sharedStrings.xml'))]
  sheet=ET.fromstring(z.read(next(n for n in z.namelist() if n.startswith('xl/worksheets/sheet') and n.endswith('.xml'))))
  assert not sheet.findall('.//s:f',ns)
  def cell(c):
   if c.attrib.get('t')=='s':return shared[int(c.find('s:v',ns).text)]
   return ''.join(t.text or '' for t in c.findall('.//s:t',ns)) if c.attrib.get('t')=='inlineStr' else c.findtext('s:v','',ns)
  rows=[[cell(c) for c in r.findall('s:c',ns)] for r in sheet.findall('.//s:row',ns)]
 assert rows[0]==['name','description','price','error']
 reads=[e for e in events if e['tool']=='browser_get_content' and not e['error']]
 assert len(rows)==len(reads)
 originals=reads[0]['result']['observationSnapshot']['pages']; offered={r.get('href') for p in originals for r in p['records']}
 for row,event in zip(rows[1:],reads[1:]):
  assert event['arguments']['url'] in offered
  snapshot=event['result']['observationSnapshot']; assert not snapshot['captureTruncated'] and not snapshot['manifestTruncated']
  records=[r for p in snapshot['pages'] for r in p['records']]
  for position,tag in enumerate(['h1','p','price']):
   facts=[r['text'] for r in records if r.get('tag')==tag]
   assert len(facts)<=1
   assert row[position]==(facts[0] if facts else '')
  assert row[3]==(event['result'].get('error_message') or '')
 assert events[-1]['tool']=='browser_close' and not events[-1]['error']
 reports.append({'variant':variant,'rows':len(rows),'visits':len(reads)-1,'cells_exact':True,'formula_cells':0,'cleanup':True,'xlsx_sha256':hashlib.sha256(file.read_bytes()).hexdigest()})
print(json.dumps(reports,indent=2))
