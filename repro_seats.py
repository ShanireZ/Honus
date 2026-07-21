import sqlite3
con = sqlite3.connect(":memory:")
cur = con.cursor()
cur.executescript("""
CREATE TABLE seats (exam_id TEXT NOT NULL, seat_id TEXT NOT NULL, student_id TEXT, machine_id TEXT, agent_id TEXT, display_name TEXT, PRIMARY KEY (exam_id, seat_id));
CREATE TABLE agent_heartbeats (exam_id TEXT, seat_id TEXT, ts REAL);
CREATE TABLE events (id INTEGER PRIMARY KEY AUTOINCREMENT, exam_id TEXT NOT NULL, seat_id TEXT NOT NULL, agent_id TEXT, machine_id TEXT, seq INTEGER, ts REAL, recv_ts REAL, type TEXT, payload TEXT, risk INTEGER DEFAULT 0, server_risk INTEGER, evidence_image_id TEXT, hash_prev TEXT, hash_self TEXT, sig TEXT);
CREATE TABLE suspicious_queue (id INTEGER PRIMARY KEY AUTOINCREMENT, exam_id TEXT, seat_id TEXT, ts REAL, kind TEXT, score INTEGER, status TEXT, source TEXT, refs TEXT, reviewer TEXT, decided_at REAL, note TEXT);
CREATE TABLE oidc_sessions (exam_id TEXT, seat_id TEXT, sub TEXT, username TEXT, nickname TEXT, dao_name TEXT, avatar TEXT, realm TEXT, realm_level INTEGER, combat_power INTEGER, user_type TEXT, issued_at REAL);
""")
# insert a couple of rows
cur.execute("INSERT INTO seats(exam_id,seat_id,student_id,machine_id,agent_id,display_name) VALUES('e1','s1','st1','m1','a1','Disp')")
cur.execute("INSERT INTO events(exam_id,seat_id,agent_id,machine_id,seq,ts,recv_ts,type,payload,risk,server_risk) VALUES('e1','s1','a1','m1',1,1000.0,1000.0,'browser_url','{}',30,40)")
cur.execute("INSERT INTO agent_heartbeats(exam_id,seat_id,ts) VALUES('e1','s1',1000.0)")
e = "e1"
rc = 0.0
sql = """
WITH hb AS (
  SELECT seat_id, MAX(ts) AS last_hb FROM agent_heartbeats WHERE exam_id=? GROUP BY seat_id
),
ev AS (
  SELECT seat_id,
         MAX(ts) AS last_ev,
         MAX(risk) AS max_agent_risk,
         MAX(COALESCE(server_risk,0)) AS max_server_risk,
         COUNT(*) AS ev_count
  FROM events WHERE exam_id=? AND ts>=? GROUP BY seat_id
),
hlt AS (
  SELECT seat_id, COUNT(*) AS health_count
  FROM events WHERE exam_id=?
    AND type IN ('watchdog_restart','suspected_suspend','screenshot_obscured','capability_degraded')
  GROUP BY seat_id
),
sq AS (
  SELECT seat_id, COUNT(*) AS susp_count
  FROM suspicious_queue WHERE exam_id=? AND status='pending' AND source='suspicion'
  GROUP BY seat_id
),
sess AS (
  SELECT exam_id, seat_id, sub, username, nickname, dao_name, avatar, realm, realm_level, combat_power, user_type,
         MAX(issued_at) AS mx
  FROM oidc_sessions WHERE exam_id=? GROUP BY exam_id, seat_id
)
SELECT s.seat_id, s.student_id, s.display_name, s.agent_id, s.machine_id,
       hb.last_hb, ev.last_ev,
       COALESCE(MAX(ev.max_agent_risk, ev.max_server_risk),0) AS max_risk,
       ev.ev_count,
       COALESCE(sq.susp_count,0),
       sess.sub, sess.username, sess.nickname, sess.dao_name, sess.avatar, sess.realm, sess.realm_level, sess.combat_power, sess.user_type,
       COALESCE(hlt.health_count,0)
FROM seats s
LEFT JOIN hb  ON hb.seat_id=s.seat_id
LEFT JOIN ev  ON ev.seat_id=s.seat_id
LEFT JOIN sq  ON sq.seat_id=s.seat_id
LEFT JOIN hlt ON hlt.seat_id=s.seat_id
LEFT JOIN sess ON sess.exam_id=s.exam_id AND sess.seat_id=s.seat_id
WHERE s.exam_id=? ORDER BY s.seat_id
"""
try:
    cur.execute(sql, (e,e,rc,e,e,e,e))
    for row in cur.fetchall():
        print("ROW:", row)
    print("OK - query ran")
except Exception as ex:
    print("SQL ERROR:", repr(ex))
