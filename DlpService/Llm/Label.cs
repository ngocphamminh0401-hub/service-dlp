// [Thay đổi] File mới (mục 6.5 - chốt tập đóng nhãn): tương ứng 1-1 với enum Label trong contracts/inference.proto
// (GIỮ NGUYÊN tên và giá trị số — đổi 1 bên mà quên đổi bên kia là lệch hợp đồng). Gộp 56 rule trong rules/ thành
// các nhóm đủ rộng cho LLM gán nhãn; rule_id cụ thể vẫn đi qua Verdict/Finding, không lặp lại ở đây.
namespace DlpService.Llm;

/// <summary>Tập đóng nhãn LLM có thể gán cho 1 file — khớp enum Label trong contracts/inference.proto.</summary>
public enum Label
{
    /// <summary>Không có gì nhạy cảm.</summary>
    None = 0,

    /// <summary>CCCD/CMND, hộ chiếu (rule: vn_id, passport).</summary>
    PiiNationalId = 1,

    /// <summary>Số điện thoại (rule: phone).</summary>
    PiiPhone = 2,

    /// <summary>Email cá nhân (rule: email, vnpt_email).</summary>
    PiiEmail = 3,

    /// <summary>Họ tên (rule: vn_name).</summary>
    PiiFullName = 4,

    /// <summary>Ngày sinh (rule: dob).</summary>
    PiiDateOfBirth = 5,

    /// <summary>Dữ liệu sinh trắc học (rule: biometric).</summary>
    PiiBiometric = 6,

    /// <summary>Dân tộc/tôn giáo/chính kiến (rule: ethnicity_religion_political).</summary>
    PiiSensitiveAttribute = 7,

    /// <summary>Số bảo hiểm xã hội (rule: social_insurance).</summary>
    PiiSocialInsurance = 8,

    /// <summary>Dữ liệu định vị/theo dõi vị trí (rule: location_tracking).</summary>
    PiiLocationTracking = 9,

    /// <summary>Số tài khoản ngân hàng/IBAN/SWIFT (rule: bank_account, iban, swift_bic).</summary>
    FinanceBankAccount = 10,

    /// <summary>Số thẻ tín dụng/CVV (rule: credit_card, cvv).</summary>
    FinanceCreditCard = 11,

    /// <summary>Mã số thuế cá nhân/doanh nghiệp (rule: tax_code_business, tax_code_personal).</summary>
    FinanceTaxCode = 12,

    /// <summary>Lương/thu nhập (rule: income).</summary>
    FinanceSalary = 13,

    /// <summary>Hồ sơ điều tra/cảnh báo gian lận (rule: fraud_investigation, fraud_transaction_flag).</summary>
    FinanceFraudCase = 14,

    /// <summary>Báo cáo tài chính nội bộ (rule: fin_*, insurance_context).</summary>
    FinanceInternalReport = 15,

    /// <summary>Mật khẩu/API key/OTP (rule: credentials, otp_auth, otp_table_header).</summary>
    SecretCredential = 16,

    /// <summary>Mã nội bộ/địa chỉ IP nội bộ (rule: internal_code, internal_ip).</summary>
    SecretInternalCode = 17,

    /// <summary>Hợp đồng (rule: contract).</summary>
    LegalContract = 18,

    /// <summary>Tài liệu mật/phân loại (rule: classified_doc).</summary>
    LegalClassifiedDocument = 19,

    /// <summary>Watermark/đánh dấu tài liệu (rule: watermark).</summary>
    LegalWatermark = 20,

    /// <summary>Thông tin sức khoẻ (rule: health, healthcare_internal_business).</summary>
    HrHealth = 21,

    /// <summary>Hồ sơ nhân sự (rule: hr_offer_letter, hr_party_membership_dossier, hr_internal_business, hr_internal_chat_protected_attributes).</summary>
    HrPersonnelRecord = 22,

    /// <summary>Vụ việc kỷ luật/khiếu nại (rule: hr_disciplinary_health_investigation, hr_sexual_harassment_complaint, hr_workplace_accident_investigation).</summary>
    HrDisciplinaryCase = 23,

    /// <summary>Thông tin kinh doanh nội bộ (rule: business_internal, business_internal_specific).</summary>
    OrgInternalBusiness = 24,

    /// <summary>Thông tin khách hàng VIP (rule: vip_customer).</summary>
    OrgVipCustomer = 25
}
