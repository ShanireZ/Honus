namespace Honus.Agent.Transport;

/// 把信号源的 CaptureReason 映射为 api-contract §2.1 约定的 trigger 取值。
/// 否则生产库 images.trigger 会是 browser_non_whitelist_url 之类的脏值,与契约/归档/统计错配。
public static class TriggerMap
{
    public static string ToContract(string reason) => reason switch
    {
        "baseline_random" => "baseline_random",
        "browser_non_whitelist_url" or "browser_url_unreadable" => "event:browser",
        "non_whitelist_process" => "event:process",
        "large_paste" => "event:paste",
        "usb_insert" => "event:usb",
        _ => "event:manual",   // capture_now 及未知原因 → 手动/其它
    };
}
