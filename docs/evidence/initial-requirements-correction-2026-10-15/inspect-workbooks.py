from pathlib import Path
import json,gzip,zipfile,xml.etree.ElementTree as E,hashlib,sys
source=Path(sys.argv[1])
reports=[]
for variant in ['complete','missing','repeated']:
 book=source/('workbook-'+variant+'.xlsx'); observation=source/('observations-'+variant+'.json.gz')
 if not book.exists(): continue
 observed=gzip.decompress(observation.read_bytes())
 events=json.loads(observed)
 acquisitions=[e for e in events if e['tool']=='browser_get_content']
 visits=acquisitions[1:]
 assert len(visits)==10
 with zipfile.ZipFile(book) as archive:
  sheet=E.fromstring(archive.read('xl/worksheets/sheet1.xml'))
  assert not sheet.findall('.//{*}f')
  rows=[[''.join(c.itertext()) for c in row] for row in sheet.findall('.//{*}row')]
 assert rows[0]==['name','description','price','error'] and len(rows)==11
 for row,event in zip(rows[1:],visits):
  snapshot=event['result']['observationSnapshot']
  assert not snapshot.get('captureTruncated',False)
  records=[r for page in snapshot['pages'] for r in page['records']]
  expected=[]
  for tag in ['h1','p','price']:
   matches=[r['text'] for r in records if r['tag']==tag]
   assert len(matches)<=1
   expected.append(matches[0] if matches else '')
  assert row==expected+[''],(variant,row,expected)
 writer=next(e for e in events if e['tool']=='document_write')
 assert writer['result']['success'] and not writer['error']
 assert writer['result']['filePath'].endswith(writer['arguments']['filePath'])
 assert events[-1]['tool']=='browser_close' and events[-1]['result']['closed'] and events[-1]['result']['success']
 if variant=='missing': assert rows[3][2]=='' and rows[4][1]==''
 if variant=='repeated': assert rows[1]==rows[-1] and visits[0]['arguments']['url']==visits[-1]['arguments']['url']
 reports.append({'variant':variant,'passed':True,'rows_with_header':len(rows),'visits':len(visits),'complete_pages':sum(len(e['result']['observationSnapshot']['pages']) for e in acquisitions),'exact_cells_compared_to_original_observations':True,'formula_count':0,'cleanup':True,'writer_path_matches_requested_suffix':True,'workbook_sha256':hashlib.sha256(book.read_bytes()).hexdigest(),'observation_sha256':hashlib.sha256(observed).hexdigest()})
assert len(reports)==2
print(json.dumps(reports,indent=2))
