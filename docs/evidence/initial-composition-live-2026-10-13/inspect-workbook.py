# Read-only XLSX/source verification. Inputs are exports made through public encrypted-storage APIs.
import json, pathlib, zipfile, xml.etree.ElementTree as ET, hashlib, re, sys
if len(sys.argv) != 3:
 raise SystemExit('Usage: inspect-workbook.py <private-evidence-directory> <workbook.xlsx>')
root=pathlib.Path(sys.argv[1])
run=json.loads((root/'retained-run-private.json').read_text())
decisions=json.loads((root/'decisions-private.json').read_text())
path=pathlib.Path(sys.argv[2]).resolve()
ns={'s':'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
findings=[]
def check(ok,code):
 if not ok:findings.append(code)
def source_records(event):
 result=event['result']; snapshot=result['observationSnapshot']; pages=snapshot['pages']
 check(result['success'] is True and result['truncated'] is False and snapshot['captureTruncated'] is False and snapshot['manifestTruncated'] is False,'incomplete_snapshot')
 records=[]
 for page in pages:
  check(page['id']==snapshot['id'] and page['captureTruncated'] is False and page.get('nextCursor') is None,'incomplete_page')
  records.extend(page['records'])
 check(len(records)==snapshot['recordCount'],'record_count_mismatch')
 check(len({x['reference'] for x in records})==len(records),'ambiguous_original_identity')
 return {x['reference']:x for x in records}
def business(decision):
 return json.loads(decision['input']['prompt'].split('Business data (JSON):',1)[1])
def offered_candidates(decision):
 data=business(decision)
 for k in ['offeredCandidates','offeredFactCandidates']:
  if k in data:return data[k]
 raise ValueError('Unknown compact input envelope: '+str(list(data)))
def column(ref):
 n=0
 for c in re.match('[A-Z]+',ref)[0]:n=n*26+ord(c)-64
 return n
check(path.is_file(),'workbook_missing')
if path.is_file():
 with zipfile.ZipFile(path) as book:
  strings=[]
  if 'xl/sharedStrings.xml' in book.namelist():
   strings=[''.join(t.text or '' for t in x.findall('.//s:t',ns)) for x in ET.fromstring(book.read('xl/sharedStrings.xml')).findall('s:si',ns)]
  rows=[]
  for sheet in sorted(n for n in book.namelist() if re.fullmatch(r'xl/worksheets/sheet\d+\.xml',n)):
   for xmlrow in ET.fromstring(book.read(sheet)).findall('.//s:sheetData/s:row',ns):
    cells={}; next_column=1
    for c in xmlrow.findall('s:c',ns):
     check(c.find('s:f',ns) is None,'unexpected_formula')
     v=c.find('s:v',ns); value='' if v is None else (v.text or '')
     if c.get('t')=='s':value=strings[int(value)]
     if c.get('t')=='inlineStr':value=''.join(t.text or '' for t in c.findall('.//s:is//s:t',ns))
     cell_column=column(c.get('r')) if c.get('r') else next_column
     cells[cell_column]=value; next_column=cell_column+1
    rows.append([cells.get(i,'') for i in range(1,max(cells,default=6)+1)])
 check(rows[0]==['name','description','price','url','status','error'],'header_mismatch')
 selection=next(d for d in decisions if (d.get('output') or {}).get('json',{}).get('selectedIds') is not None)
 ids=selection['output']['json']['selectedIds']; offered=offered_candidates(selection)
 check(1<=len(ids)<=10 and len(rows)==len(ids)+1,'row_or_product_count_mismatch')
 acquisitions=[e for e in run['events'] if e.get('result',{}).get('observationSnapshot')]
 original=source_records(acquisitions[0]); visits=acquisitions[1:]
 check(len(visits)==len(ids),'visit_count_mismatch')
 check(all(sum(c['reference']==i for c in offered)==1 for i in ids),'selection_not_unambiguously_offered')
 evidence=[]
 for index,(ident,visit,row) in enumerate(zip(ids,visits,rows[1:])):
  check(ident in original,f'product_{index+1}_unknown_original')
  record=original[ident]; url=record['href']; result=visit['result']; recordmap=source_records(visit)
  check(visit['arguments']['url']==url and row[3]==url,f'product_{index+1}_url_not_exact')
  if url!=result['url']:check(result.get('acquisition',{}).get('navigation',{}).get('url')==result['url'],f'product_{index+1}_unproven_redirect')
  decision=next(d for d in decisions if '/mapping/' not in d['id'] and f'/iteration/{index}/' in d['id'] and 'descriptionId' in (d.get('output') or {}).get('json',{}))
  facts=decision['output']['json']; candidates=offered_candidates(decision); exact={}
  for cell,key in enumerate(['nomId','descriptionId','prixId']):
   value=facts[key]
   if value is None:
    exact[key]=row[cell]=='';check(exact[key],f'product_{index+1}_{key}_nonblank_missing');findings.append(f'product_{index+1}_{key}_absence_requires_independent_review')
   else:
    check(value.strip()!='' and sum(c['reference']==value for c in candidates)==1,f'product_{index+1}_{key}_invalid_offered_id')
    check(value in recordmap,f'product_{index+1}_{key}_unknown_original')
    exact[key]=value in recordmap and row[cell]==recordmap[value]['text'];check(exact[key],f'product_{index+1}_{key}_not_exact_original')
  check(row[4]==json.dumps(result['success']),f'product_{index+1}_status_not_producer')
  check(row[5]==(result.get('error_message') or ''),f'product_{index+1}_error_not_producer')
  evidence.append({'product':index+1,'observed_name':row[0],'description_characters':len(row[1]),'description_sha256':hashlib.sha256(row[1].encode()).hexdigest(),'price':row[2],'url_sha256':hashlib.sha256(url.encode()).hexdigest(),'redirected':url!=result['url'],'source_pages':len(result['observationSnapshot']['pages']),'source_records':len(recordmap),'offered_fact_candidates':len(candidates),'exact_selected_fields':exact,'status':row[4],'error':row[5]})
 writer=[e for e in run['events'] if e.get('tool')=='document_write'];check(len(writer)==1 and writer[0]['result']['success'] is True and pathlib.Path(writer[0]['result']['filePath'])==path,'writer_receipt_mismatch')
 check(run['execution']['success'] is True and pathlib.Path(run['execution']['outputs']['filePath'])==path,'public_output_path_mismatch')
 result={'passed':not findings,'findings':findings,'read_only':True,'provider_calls':0,'mcp_calls':0,'workbook_path':str(path),'workbook_sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'workbook_bytes':path.stat().st_size,'row_count':len(rows),'headers':rows[0],'products':evidence,'complete_acquisitions':len(acquisitions),'source_records':sum(e['result']['observationSnapshot']['recordCount'] for e in acquisitions),'raw_snapshot_in_global_interpretation':any('observationSnapshot' in business(d) for d in decisions if '/mapping/' not in d['id'] and d.get('input'))}
 (root/'independent-workbook.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n');(root/'workbook-cells-private.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2)+'\n');print(json.dumps(result,ensure_ascii=False,indent=2))
else:print(json.dumps({'passed':False,'findings':findings}))
