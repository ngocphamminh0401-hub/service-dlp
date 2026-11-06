# [Thay đổi] File mới: sinh file .xls (Excel 97-2003) từ spec JSON do DlpTestGen (C#) ghi ra.
# Dùng xlwt (BSD) vì NPOI yêu cầu chấp nhận giấy phép OSMF EULA — quyết định đó để người dùng tự cân nhắc.
# Cách dùng: python gen_xls.py spec.json out.xls
# spec: {"sheets":[{"name":"S1","hidden":false,"rows":[["a","b"],["c","d"]]}]}
import json
import sys

import xlwt

spec = json.load(open(sys.argv[1], encoding="utf-8"))
wb = xlwt.Workbook(encoding="utf-8")
for s in spec["sheets"]:
    ws = wb.add_sheet(s["name"])
    for r, row in enumerate(s["rows"]):
        for c, val in enumerate(row):
            ws.write(r, c, val)
    if s.get("hidden"):
        ws.sheet_visible = 0  # sheet ẩn: dữ liệu nhạy cảm hay bị giấu ở đây
wb.save(sys.argv[2])
