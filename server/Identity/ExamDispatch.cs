using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Identity;

/// 考试派发(owner 决策 2026-07-03):examId 不再由 Agent 配置携带,而是**服务端指派** ——
/// 「当前活跃考试」= exams 表 status='active' 中最近创建的一场(正常运营同一时刻只应有一场 active)。
/// /oidc/exchange 建会话与 /oidc/active-exam 待命轮询共用此单一实现,杜绝两处判据漂移。
public static class ExamDispatch
{
    public sealed record ActiveExam(string ExamId, string Name);

    public static ActiveExam? ResolveActive(Db db)
        => db.Read<ActiveExam?>(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                "SELECT exam_id,name FROM exams WHERE status='active' ORDER BY created_at DESC, exam_id DESC LIMIT 1");
            using SqliteDataReader r = c.ExecuteReader();
            return r.Read() ? new ActiveExam(r.GetString(0), r.GetString(1)) : null;
        });

    /// seatId 派生:= OIDC username(权威身份,看板/取证按人显示);username 为空或含路径危险字符时回退 sub。
    /// 「座位号」不再是学员手工配置的教室座位概念 —— 物理定位由 machineId/agentId 承担。
    public static string SeatFrom(OidcClaims claims)
        => !string.IsNullOrEmpty(claims.Username) && Api.Endpoints.IsSafeId(claims.Username)
            ? claims.Username
            : claims.Sub;
}
