# Kiểm kê bộ rule (Benchmark T01)

- Thời điểm: 2026-11-18 15:22 · thư mục: `/home/servicelinux/projects/service-dlp/tools/perf/DlpRuleInventory/bin/Release/net10.0/rules` · rules_ver: `5299F13D9F40`
- Nguồn: đối tượng `SensitivePattern` sau `RuleLoader.LoadFromFolder` (đúng như service), không phải đếm thô JSON.

## 1. Tổng quan

| Mục | Đo được | Code Review |
|---|---|---|
| File rule (trừ compound.json) | 63 | 63 |
| Rule (khoá RuleId ?? Name) | 54 | — |
| Pattern đã nạp | 222 | 221 |
| Từ khoá — tổng theo rule (đã bỏ trùng trong rule) | 978 | 1.134 |
| Từ khoá — trong đó có dấu | 636 | 690 |
| Từ khoá — khác nhau toàn bộ | 868 (có dấu 564) | — |
| Regex blocklist (placeholder) | 7 | — |

## 2. Cờ theo rule / pattern

| Cờ | Số rule (≥1 pattern bật) | Số pattern | Code Review (rule) |
|---|---|---|---|
| context_required | 35 | 67 | 62/63 |
| exclude_if_no_keywords | 28 | 132 | 51 |
| proximity (pattern hoặc rule) | 8 | 26 |  |
| proximity_window cấp rule | 6 | 22 |  |
| allow_table_fallback | 6 | 22 |  |
| negation_filter | 8 | 41 |  |
| escalation | 3 | 5 |  |
| volume_escalation | 5 | 19 |  |
| placeholder_exclusion | 6 | 26 |  |
| level_gate | 40 | 168 |  |
| validator (mọi loại) | 9 | 26 |  |
| validator checksum | 6 | 16 |  |
| cvv_expiry_boost | 1 | 7 |  |

## 3. Cận trên số lượt dò từ khoá trên TOÀN nội dung (1 file)

Mỗi lượt = 1 lần `string.Contains(k, OrdinalIgnoreCase)` trên cả nội dung; trường hợp xấu nhất (từ khoá không có) đi hết chuỗi.

| Nguồn (RegexScanner) | Điều kiện | Số lượt tối đa |
|---|---|---|
| `BuildTelemetry` (primary của mọi rule + 10 DocContextProbe) | mọi file quét trọn | 504 |
| `RuleHasKeyword` (Keywords của rule exclude_if_no_keywords, cache theo rule) | rule đó có ≥1 chỗ khớp | 594 |
| `DocDiscountFactor` (cache theo PlaceholderRule) | có chỗ khớp ở rule có placeholder | 33 |
| vòng `deferred` (ProximityKeywords, KHÔNG cache) | × số chỗ khớp deferred | ≤ 26 / chỗ khớp |
| **Tổng tĩnh** (chưa tính deferred) | | **1131** |

Ghi chú: con số "555 lượt" của Code Review so với dòng tổng tĩnh ở trên; phần deferred phụ thuộc dữ liệu và được đo ở R2/R3.

## 4. Chi tiết theo rule

| Rule | Category | Pattern | Từ khoá (context/primary/secondary) | context_req | excl_no_kw | proximity | table_fb | validator |
|---|---|---|---|---|---|---|---|---|
| CreditCard |  | 1 | 0/0/0 |  |  |  |  | all |
| bank_account_001 | financial | 4 | 49/12/37 | all | all | all | all |  |
| biometric_001 | pii | 4 | 24/14/10 | 1/4 |  |  |  |  |
| business_internal_001 | org | 5 | 33/23/10 |  | all |  |  |  |
| business_internal_specific_001 | org | 3 | 30/26/4 |  | all |  |  |  |
| classified_doc_001 | org | 3 | 23/14/9 |  |  |  |  |  |
| classified_doc_002 | org | 3 | 19/9/10 | 1/3 |  |  |  |  |
| contract_001 | org | 1 | 37/9/28 |  | all |  |  |  |
| credentials_001 | org | 6 | 42/22/20 | 1/6 |  |  |  | 3/6 |
| credit_card_001 | financial | 7 | 19/7/12 |  |  |  |  | all |
| cvv_001 | financial | 2 | 16/8/8 |  |  |  |  |  |
| dob_001 | pii | 5 | 22/11/11 | all | all | 3/5 |  |  |
| email_001 | pii | 3 | 11/5/6 |  |  |  |  | all |
| ethnicity_religion_political_001 | pii | 4 | 16/6/10 |  | all |  |  |  |
| fin_account_statement_001 | financial | 3 | 9/5/4 |  |  |  |  |  |
| fin_compliance_report_001 | financial | 3 | 9/4/5 | 2/3 | all |  |  |  |
| fin_credit_card_statement_001 | financial | 3 | 8/4/4 |  |  |  |  |  |
| fin_internal_branch_financials_001 | financial | 3 | 9/5/4 | 2/3 |  |  |  |  |
| fin_internal_chat_customer_data_001 | financial | 3 | 9/4/5 | 2/3 |  |  |  |  |
| fin_internal_risk_policy_discussion_001 | financial | 3 | 8/4/4 | 2/3 | all |  |  |  |
| fin_internal_training_guide_001 | financial | 5 | 13/6/7 | 2/5 | all |  |  |  |
| fin_loan_approval_process_001 | financial | 3 | 8/4/4 | 1/3 | all |  |  |  |
| fin_process_change_notice_001 | financial | 4 | 15/7/8 | 2/4 | all |  |  |  |
| fin_transfer_confirmation_001 | financial | 3 | 8/5/3 |  |  |  |  |  |
| fraud_investigation_001 | org | 5 | 17/8/9 |  |  |  |  |  |
| fraud_transaction_flag_001 | org | 4 | 9/6/3 |  |  |  |  |  |
| health_001 | pii | 13 | 41/25/16 | 4/13 | all |  |  |  |
| healthcare_internal_business_001 | org | 13 | 22/17/5 | 1/13 | all |  |  |  |
| hr_disciplinary_health_investigation_001 | hr | 3 | 10/5/5 | 2/3 |  |  |  |  |
| hr_internal_business_001 | hr | 18 | 33/25/8 | 3/18 | all |  |  |  |
| hr_internal_chat_protected_attributes_001 | hr | 3 | 11/6/5 | 1/3 |  |  |  |  |
| hr_offer_letter_001 | hr | 5 | 9/4/5 | 3/5 | all |  |  |  |
| hr_party_membership_dossier_001 | hr | 4 | 11/5/6 | 1/4 |  |  |  |  |
| hr_sexual_harassment_complaint_001 | hr | 4 | 10/5/5 | 1/4 | all |  |  |  |
| hr_workplace_accident_investigation_001 | hr | 3 | 9/4/5 | 2/3 | all |  |  |  |
| iban_001 | financial | 2 | 15/4/11 | all | all |  |  | all |
| income_001 | financial | 4 | 26/11/15 | 1/4 | all | all | all |  |
| insurance_context_001 | org | 4 | 14/11/3 |  | all |  |  |  |
| internal_code_001 | org | 5 | 19/9/10 | 1/5 |  |  |  |  |
| internal_ip_001 | org | 1 | 21/7/14 |  |  |  |  |  |
| location_tracking_001 | pii | 4 | 14/2/12 | 2/4 | all | 1/4 |  |  |
| otp_auth_001 | financial | 5 | 11/5/6 | 2/5 |  |  |  |  |
| otp_table_header_001 | financial | 1 | 6/3/3 |  |  |  |  |  |
| passport_001 | pii | 5 | 20/8/12 | 4/5 | all | all | all |  |
| phone_001 | pii | 5 | 26/10/16 | 2/5 |  |  |  | 4/5 |
| social_insurance_001 | pii | 4 | 22/11/11 | 1/4 | all | all | all |  |
| swift_bic_001 | financial | 2 | 18/7/11 | 1/2 | all |  |  |  |
| tax_code_business_001 | financial | 3 | 25/13/12 | 2/3 | all |  |  | all |
| tax_code_personal_001 | financial | 2 | 16/9/7 | 1/2 | all | all | all | all |
| vip_customer_001 | financial | 2 | 11/4/7 |  |  |  |  |  |
| vn_id_001 | pii | 3 | 28/15/13 | 2/3 | all | all | all | 1/3 |
| vn_name_001 | pii | 3 | 32/18/14 | 2/3 | all |  |  |  |
| vnpt_email_001 | org | 1 | 20/11/9 |  |  |  |  |  |
| watermark_001 | org | 7 | 19/12/7 | 1/7 |  |  |  |  |
