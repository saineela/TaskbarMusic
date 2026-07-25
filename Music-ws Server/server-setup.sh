#!/usr/bin/env bash
set -euo pipefail

###################################################################
#  TaskbarMusic – Full Installer / Upgrader
#  - WebSocket relay (multi-pair + global LRC cache)
#  - Web UI (user accounts, pair management, Docker logs, LRC cache)
#  - Terminal manager (taskbarmusic)
#  Preserves all existing data: users, pairs, lyrics, settings.
###################################################################

WORKDIR="/opt/music-ws"
APP_DIR="$WORKDIR/app"
WEB_DIR="$WORKDIR/webapp"
LRC_DIR="$WORKDIR/lrc_cache"
BACKUP_DIR="$WORKDIR/backups"
COMPOSE_RELAY="$WORKDIR/docker-compose.yml"
COMPOSE_WEB="$WEB_DIR/docker-compose.web.yml"
CONTAINER_RELAY="music-ws"
CONTAINER_WEB="music-ws-web"
BIN_SCRIPT="/usr/local/bin/taskbarmusic"

echo "============================================"
echo "  TaskbarMusic Full Installer / Upgrader"
echo "============================================"

# --------------- prerequisites ---------------
if ! command -v docker &>/dev/null; then
    echo "ERROR: Docker is not installed. Install it with:"
    echo "  curl -fsSL https://get.docker.com | sh"
    exit 1
fi
if ! command -v docker compose &>/dev/null && ! command -v docker-compose &>/dev/null; then
    echo "ERROR: Docker Compose plugin not found."
    exit 1
fi

if ! command -v jq &>/dev/null; then
    echo "Installing jq..."
    apt-get update -qq && apt-get install -y -qq jq || {
        echo "ERROR: jq required. Install manually: apt install jq"
        exit 1
    }
fi

# --------------- helpers ---------------
backup_file() {
    local src="$1"
    local ts=$(date +%s)
    if [ -f "$src" ]; then
        mkdir -p "$BACKUP_DIR"
        cp "$src" "$BACKUP_DIR/$(basename "$src").backup-$ts"
        echo "  ✓ backed up $src"
    fi
}

stop_container() {
    if docker ps -q --filter "name=$1" | grep -q .; then
        echo "Stopping $1..."
        docker stop "$1" 2>/dev/null || true
    fi
}

# --------------- 1. Prepare directories & stop containers ---------------
echo ">>> Creating directory structure..."
mkdir -p "$APP_DIR" "$BACKUP_DIR" "$LRC_DIR" "$WEB_DIR/data"

stop_container "$CONTAINER_RELAY"
stop_container "$CONTAINER_WEB"

# --------------- 2. Backup critical files ---------------
backup_file "$APP_DIR/server.js"
backup_file "$APP_DIR/.env"
backup_file "$APP_DIR/pairs.json"
backup_file "$COMPOSE_RELAY"
backup_file "$WEB_DIR/app.py"
backup_file "$WEB_DIR/data/users.db"

# --------------- 3. Preserve / create pairs.json ---------------
echo ""
echo ">>> Managing pairs.json"
if [ -f "$APP_DIR/pairs.json" ] && [ -s "$APP_DIR/pairs.json" ]; then
    if jq -e '.pairs | type == "array"' "$APP_DIR/pairs.json" >/dev/null 2>&1; then
        echo "  Found $(jq '.pairs | length' "$APP_DIR/pairs.json") existing pair(s) – keeping them safe."
    else
        echo "  ⚠ pairs.json corrupted. Restoring from backup..."
        latest=$(ls -t "$BACKUP_DIR"/pairs.json.backup-* 2>/dev/null | head -1)
        if [ -n "$latest" ]; then
            cp "$latest" "$APP_DIR/pairs.json"
            echo "  Restored from $latest"
        else
            echo '{"pairs":[]}' > "$APP_DIR/pairs.json"
        fi
    fi
else
    # Try to migrate from legacy .env
    if [ -f "$APP_DIR/.env" ]; then
        PHONE=$(grep -E '^PHONE_TOKEN=' "$APP_DIR/.env" | cut -d'=' -f2- | tr -d '"' | tr -d "'" | head -1)
        LAPTOP=$(grep -E '^LAPTOP_TOKEN=' "$APP_DIR/.env" | cut -d'=' -f2- | tr -d '"' | tr -d "'" | head -1)
        if [ -n "$PHONE" ] && [ -n "$LAPTOP" ]; then
            jq -n --arg name "Default Pair" --arg phone "$PHONE" --arg laptop "$LAPTOP" \
               '{pairs: [{name: $name, phoneToken: $phone, laptopToken: $laptop}]}' > "$APP_DIR/pairs.json"
            echo "  Migrated legacy pair → Default Pair"
        else
            echo '{"pairs":[]}' > "$APP_DIR/pairs.json"
        fi
    else
        echo '{"pairs":[]}' > "$APP_DIR/pairs.json"
    fi
fi

# --------------- 4. WebSocket relay (with global LRC cache) ---------------
echo ""
echo ">>> Updating WebSocket relay (global LRC cache)"
cat > "$APP_DIR/server.js" << 'RELAYEOF'
const WebSocket = require("ws");
const url = require("url");
const fs = require("fs");
const path = require("path");

const PORT = 8090;
const PAIRS_PATH = path.join(__dirname, "pairs.json");
const LRC_DIR = "/opt/music-ws/lrc_cache";

if (!fs.existsSync(LRC_DIR)) fs.mkdirSync(LRC_DIR, { recursive: true });

function loadPairs() {
  try {
    return JSON.parse(fs.readFileSync(PAIRS_PATH, "utf8")).pairs || [];
  } catch (err) { console.error(err); return []; }
}

const pairStates = new Map();
function getState(phoneToken, name) {
  if (!pairStates.has(phoneToken)) {
    pairStates.set(phoneToken, { name, phoneClients: new Set(), laptopClients: new Set() });
  }
  return pairStates.get(phoneToken);
}

const wss = new WebSocket.Server({ port: PORT });
console.log("Music WS (multi-pair + global LRC) on port " + PORT);

function send(client, msg) { if (client.readyState === WebSocket.OPEN) client.send(JSON.stringify(msg)); }
function sanitizeFilename(name) { return name.replace(/[<>:"/\\|?*\x00-\x1f]/g, '_').trim(); }
function lrcPath(song) { return path.join(LRC_DIR, sanitizeFilename(song) + ".lrc"); }

function handleLrcUpload(pair, data) {
  const { song, content } = data;
  if (!song || typeof content !== 'string') return;
  fs.writeFileSync(lrcPath(song), content, 'utf8');
  console.log(`[${pair.name}] LRC uploaded: ${song}`);
}
function handleLrcRequest(ws, pair, data) {
  const { song } = data;
  if (!song) return;
  const fp = lrcPath(song);
  if (fs.existsSync(fp)) {
    send(ws, { type: "lrc_response", song, content: fs.readFileSync(fp, 'utf8') });
    console.log(`[${pair.name}] LRC served: ${song}`);
  } else {
    send(ws, { type: "lrc_response", song, content: null, error: "not_found" });
    console.log(`[${pair.name}] LRC not found: ${song}`);
  }
}

wss.on("connection", (ws, req) => {
  const token = url.parse(req.url, true).query.token || "";
  const pairs = loadPairs();
  let pair = null, role = null;
  for (const p of pairs) {
    if (token === p.phoneToken) { pair = p; role = "phone"; break; }
  }
  if (!pair) {
    for (const p of pairs) {
      if (token === p.laptopToken) { pair = p; role = "laptop"; break; }
    }
  }
  if (!pair) { ws.close(1008, "Unauthorized"); return; }

  const state = getState(pair.phoneToken, pair.name);
  if (role === "phone") state.phoneClients.add(ws);
  else state.laptopClients.add(ws);

  console.log(`[${pair.name}] ${role.toUpperCase()} connected | P=${state.phoneClients.size} L=${state.laptopClients.size}`);
  send(ws, { type: "connected", role, pair: pair.name });

  ws.on("message", raw => {
    let msg;
    try { msg = JSON.parse(raw.toString()); } catch { return; }

    if (msg.type === "lrc_upload") { handleLrcUpload(pair, msg); return; }
    if (msg.type === "lrc_request") { handleLrcRequest(ws, pair, msg); return; }

    // forward all other messages
    console.log(`[${pair.name}] [${role}] ${JSON.stringify(msg)}`);
    if (role === "phone") {
      state.laptopClients.forEach(c => { if (c.readyState === WebSocket.OPEN) c.send(JSON.stringify(msg)); });
    } else {
      state.phoneClients.forEach(c => { if (c.readyState === WebSocket.OPEN) c.send(JSON.stringify(msg)); });
    }
  });

  ws.on("close", () => {
    state.phoneClients.delete(ws);
    state.laptopClients.delete(ws);
  });
  ws.on("error", err => console.log(`[${pair.name}] socket error:`, err.message));
});
RELAYEOF

# --------------- 5. Terminal manager (with user management) ---------------
echo ""
echo ">>> Updating terminal manager (ws-manager.py)"
cat > "$WORKDIR/ws-manager.py" << 'PYEOF'
#!/usr/bin/env python3
import json, os, random, string, sys, sqlite3

REAL_DIR = os.path.dirname(os.path.realpath(__file__))
PAIRS_FILE = os.path.join(REAL_DIR, "app", "pairs.json")
CREDS_FILE = os.path.join(REAL_DIR, "MUSIC_WS_CREDENTIALS.txt")
WEB_DB = os.path.join(REAL_DIR, "webapp", "data", "users.db")

def load():
    if not os.path.exists(PAIRS_FILE): return {"pairs": []}
    with open(PAIRS_FILE) as f: return json.load(f)

def save(data):
    with open(PAIRS_FILE, "w") as f: json.dump(data, f, indent=2)

def generate_token():
    return ''.join(random.choices(string.hexdigits.lower(), k=16))

def write_creds(data):
    lines = []
    for p in data["pairs"]:
        lines.append(p["name"])
        lines.append(f"  Phone  : {p['phoneToken']}")
        lines.append(f"  Laptop : {p['laptopToken']}")
        lines.append("-" * 30)
    with open(CREDS_FILE, "w") as f: f.write("\n".join(lines))

def restart_container():
    os.system("docker restart music-ws 2>/dev/null || docker compose -f /opt/music-ws/docker-compose.yml restart")

def db_connect():
    if not os.path.exists(WEB_DB):
        print("Web UI database not found. Install the web UI first.")
        return None
    return sqlite3.connect(WEB_DB)

def list_users():
    conn = db_connect()
    if not conn: return
    cur = conn.cursor()
    cur.execute("SELECT id, email, is_admin FROM user")
    rows = cur.fetchall()
    if not rows:
        print("No users found."); conn.close(); return
    print("\nUsers:")
    print(f"{'ID':<5} {'Email':<35} {'Admin'}")
    print("-" * 50)
    for uid, email, admin in rows:
        print(f"{uid:<5} {email:<35} {'Yes' if admin else 'No'}")
    conn.close()

def reset_password():
    conn = db_connect()
    if not conn: return
    cur = conn.cursor()
    cur.execute("SELECT id, email FROM user")
    users = cur.fetchall()
    if not users:
        print("No users found."); conn.close(); return
    print("\nSelect user to reset password:")
    for uid, email in users:
        print(f"{uid}) {email}")
    try:
        choice = int(input("> ").strip())
    except ValueError:
        print("Invalid selection."); conn.close(); return
    cur.execute("SELECT id FROM user WHERE id=?", (choice,))
    if not cur.fetchone():
        print("User not found."); conn.close(); return
    new_password = ''.join(random.choices(string.ascii_letters + string.digits, k=12))
    try:
        from werkzeug.security import generate_password_hash
    except ImportError:
        print("Error: werkzeug not available. Install it: pip install werkzeug")
        conn.close(); return
    pw_hash = generate_password_hash(new_password)
    cur.execute("UPDATE user SET password_hash=? WHERE id=?", (pw_hash, choice))
    conn.commit(); conn.close()
    print(f"Password reset. New password: {new_password}")

def main_menu():
    while True:
        print("\n" + "="*40)
        print("        Nitro Music WS Manager")
        print("="*40)
        print("1) Create Pair")
        print("2) Delete Pair")
        print("3) Rename Pair")
        print("4) Show Tokens")
        print("5) Restart WebSocket")
        print("6) Backup pairs.json")
        print("7) Manage Users")
        print("8) Exit")
        choice = input("\n> ").strip()

        data = load()

        if choice == "1":
            name = input("Pair Name: ").strip()
            if not name:
                print("Name required."); continue
            phone = generate_token()
            laptop = generate_token()
            data["pairs"].append({"name": name, "phoneToken": phone, "laptopToken": laptop})
            save(data)
            write_creds(data)
            print(f"\nCreated {name}\nPhone Token  : {phone}\nLaptop Token : {laptop}")
            print("(No restart needed – server auto-detects new pairs)")

        elif choice == "2":
            if not data["pairs"]:
                print("No pairs."); continue
            for i, p in enumerate(data["pairs"], 1):
                print(f"{i}) {p['name']}")
            sel = input("> ").strip()
            if not sel.isdigit() or int(sel) < 1 or int(sel) > len(data["pairs"]):
                print("Invalid."); continue
            del data["pairs"][int(sel)-1]
            save(data)
            write_creds(data)
            restart_container()
            print("Deleted. Container restarted.")

        elif choice == "3":
            if not data["pairs"]:
                print("No pairs."); continue
            for i, p in enumerate(data["pairs"], 1):
                print(f"{i}) {p['name']}")
            sel = input("> ").strip()
            if not sel.isdigit() or int(sel) < 1 or int(sel) > len(data["pairs"]):
                print("Invalid."); continue
            new_name = input("New name: ").strip()
            if new_name:
                data["pairs"][int(sel)-1]["name"] = new_name
                save(data)
                write_creds(data)
                restart_container()
                print("Renamed and container restarted.")

        elif choice == "4":
            if not data["pairs"]:
                print("No pairs."); continue
            for p in data["pairs"]:
                print(f"\n{p['name']}")
                print(f"  Phone  : {p['phoneToken']}")
                print(f"  Laptop : {p['laptopToken']}")

        elif choice == "5":
            restart_container()
            print("Container restarted.")

        elif choice == "6":
            import time
            backup = PAIRS_FILE + f".backup-{int(time.time())}"
            os.system(f"cp {PAIRS_FILE} {backup}")
            print(f"Backed up to {backup}")

        elif choice == "7":
            while True:
                print("\n--- Manage Users ---")
                print("1) List Users")
                print("2) Reset User Password")
                print("3) Back")
                sub = input("> ").strip()
                if sub == "1":
                    list_users()
                elif sub == "2":
                    reset_password()
                elif sub == "3":
                    break
                else:
                    print("Invalid option.")

        elif choice == "8":
            print("Bye!"); sys.exit(0)
        else:
            print("Invalid choice.")

if __name__ == "__main__":
    main_menu()
PYEOF
chmod +x "$WORKDIR/ws-manager.py"

# --------------- 6. Docker compose for relay ---------------
echo ""
echo ">>> Creating docker-compose.yml (relay)"
cat > "$COMPOSE_RELAY" << COMPOSEREL
services:
  music-ws:
    image: node:22-alpine
    container_name: music-ws
    restart: unless-stopped
    working_dir: /app
    volumes:
      - ./app:/app
      - ./lrc_cache:/opt/music-ws/lrc_cache
    command: sh -c "npm install --omit=dev && node server.js"
    ports:
      - "8090:8090"
COMPOSEREL

# --------------- 7. Web UI application ---------------
echo ""
echo ">>> Updating web UI (Flask app with LRC cache management)"
mkdir -p "$WEB_DIR/templates"

# Generate admin password if fresh install
if [ ! -f "$WEB_DIR/data/users.db" ]; then
    ADMIN_PASSWORD=$(openssl rand -base64 12)
    echo "ADMIN_PASSWORD=$ADMIN_PASSWORD" > "$WEB_DIR/.env"
    echo "Fresh web UI install – admin password generated."
else
    [ -f "$WEB_DIR/.env" ] || echo "ADMIN_PASSWORD=none" > "$WEB_DIR/.env"
fi

cat > "$WEB_DIR/app.py" << 'PYEOF'
import os, json, random, string
from flask import Flask, render_template, redirect, url_for, request, flash
from flask_sqlalchemy import SQLAlchemy
from flask_login import (LoginManager, UserMixin, login_user, login_required,
                         logout_user, current_user)
from werkzeug.security import generate_password_hash, check_password_hash
import docker

app = Flask(__name__)
app.secret_key = os.urandom(24)
app.config['SQLALCHEMY_DATABASE_URI'] = 'sqlite:///' + os.path.join(os.path.dirname(__file__), 'data', 'users.db')
app.config['SQLALCHEMY_TRACK_MODIFICATIONS'] = False

db = SQLAlchemy(app)
login_manager = LoginManager()
login_manager.init_app(app)
login_manager.login_view = 'login'

LRC_DIR = "/opt/music-ws/lrc_cache"
os.makedirs(LRC_DIR, exist_ok=True)

# ---------- Models ----------
class User(UserMixin, db.Model):
    id = db.Column(db.Integer, primary_key=True)
    email = db.Column(db.String(120), unique=True, nullable=False)
    password_hash = db.Column(db.String(128), nullable=False)
    is_admin = db.Column(db.Boolean, default=False)

class Pair(db.Model):
    id = db.Column(db.Integer, primary_key=True)
    user_id = db.Column(db.Integer, db.ForeignKey('user.id'), nullable=False)
    name = db.Column(db.String(80), nullable=False)
    phone_token = db.Column(db.String(32), unique=True, nullable=False)
    laptop_token = db.Column(db.String(32), unique=True, nullable=False)

class Setting(db.Model):
    key = db.Column(db.String(80), primary_key=True)
    value = db.Column(db.String(255), nullable=False)

with app.app_context():
    db.create_all()
    try:
        db.session.execute('ALTER TABLE user ADD COLUMN is_admin BOOLEAN DEFAULT 0')
        db.session.commit()
    except Exception:
        pass
    # create admin if none
    admin_email = "admin@taskbarmusic.local"
    if not User.query.filter_by(is_admin=True).first():
        admin_pw = os.environ.get("ADMIN_PASSWORD")
        if admin_pw and admin_pw != "none":
            u = User.query.filter_by(email=admin_email).first()
            if not u:
                u = User(email=admin_email, password_hash=generate_password_hash(admin_pw), is_admin=True)
                db.session.add(u)
            else:
                u.is_admin = True
                u.password_hash = generate_password_hash(admin_pw)
            db.session.commit()
    if not Setting.query.get("registration_enabled"):
        db.session.add(Setting(key="registration_enabled", value="true"))
        db.session.commit()

# ---------- Helpers ----------
def generate_token(): return ''.join(random.choices('abcdef0123456789', k=16))

def regenerate_pairs_json():
    pairs = Pair.query.all()
    data = [{"name": p.name, "phoneToken": p.phone_token, "laptopToken": p.laptop_token} for p in pairs]
    with open('/opt/music-ws/app/pairs.json', 'w') as f:
        json.dump({"pairs": data}, f, indent=2)

def is_registration_enabled():
    s = Setting.query.get("registration_enabled")
    return s and s.value == "true"

@login_manager.user_loader
def load_user(user_id): return User.query.get(int(user_id))

# ---------- Routes ----------
@app.route('/')
@login_required
def index(): return redirect(url_for('dashboard'))

@app.route('/dashboard')
@login_required
def dashboard():
    pairs = Pair.query.filter_by(user_id=current_user.id).all()
    return render_template('dashboard.html', pairs=pairs)

@app.route('/login', methods=['GET', 'POST'])
def login():
    if request.method == 'POST':
        email = request.form['email']; password = request.form['password']
        user = User.query.filter_by(email=email).first()
        if user and check_password_hash(user.password_hash, password):
            login_user(user); return redirect(url_for('dashboard'))
        flash('Invalid email or password')
    return render_template('login.html', registration_enabled=is_registration_enabled())

@app.route('/register', methods=['GET', 'POST'])
def register():
    if not is_registration_enabled():
        flash('Registration is currently disabled.'); return redirect(url_for('login'))
    if request.method == 'POST':
        email = request.form['email']; password = request.form['password']
        if User.query.filter_by(email=email).first():
            flash('Email already registered')
            return render_template('register.html', registration_enabled=is_registration_enabled())
        user = User(email=email, password_hash=generate_password_hash(password))
        db.session.add(user); db.session.commit()
        login_user(user); return redirect(url_for('dashboard'))
    return render_template('register.html', registration_enabled=is_registration_enabled())

@app.route('/logout')
@login_required
def logout(): logout_user(); return redirect(url_for('login'))

@app.route('/change_password', methods=['GET', 'POST'])
@login_required
def change_password():
    if request.method == 'POST':
        old = request.form['old_password']
        new = request.form['new_password']
        confirm = request.form['confirm_password']
        if not check_password_hash(current_user.password_hash, old):
            flash('Incorrect current password.')
        elif new != confirm:
            flash('New passwords do not match.')
        elif len(new) < 6:
            flash('Password must be at least 6 characters.')
        else:
            current_user.password_hash = generate_password_hash(new)
            db.session.commit()
            flash('Password updated.')
            return redirect(url_for('dashboard'))
    return render_template('change_password.html')

@app.route('/create_pair', methods=['POST'])
@login_required
def create_pair():
    name = request.form['name'].strip()
    if not name: flash('Name required'); return redirect(url_for('dashboard'))
    phone = generate_token(); laptop = generate_token()
    pair = Pair(user_id=current_user.id, name=name, phone_token=phone, laptop_token=laptop)
    db.session.add(pair); db.session.commit()
    regenerate_pairs_json()
    flash(f'Pair "{name}" created!')
    return redirect(url_for('dashboard'))

@app.route('/delete_pair/<int:pair_id>')
@login_required
def delete_pair(pair_id):
    pair = Pair.query.get_or_404(pair_id)
    if pair.user_id != current_user.id:
        flash('Not allowed'); return redirect(url_for('dashboard'))
    db.session.delete(pair); db.session.commit()
    regenerate_pairs_json()
    flash('Pair deleted'); return redirect(url_for('dashboard'))

@app.route('/logs')
@login_required
def logs():
    try:
        client = docker.from_env()
        container = client.containers.get('music-ws')
        raw = container.logs(tail=200).decode('utf-8')
    except Exception as e:
        raw = f"Error: {e}"
    return render_template('logs.html', logs=raw)

@app.route('/admin', methods=['GET', 'POST'])
@login_required
def admin_panel():
    if not current_user.is_admin:
        flash('Access denied'); return redirect(url_for('dashboard'))
    if request.method == 'POST':
        enabled = request.form.get('registration_enabled') == 'on'
        Setting.query.get("registration_enabled").value = "true" if enabled else "false"
        db.session.commit()
        flash('Settings updated')
    return render_template('admin.html', registration_enabled=(Setting.query.get("registration_enabled").value == "true"))

# ---- LRC Cache Management (real disk usage) ----
@app.route('/lrc_cache')
@login_required
def lrc_cache():
    files = []
    total_cache_size = 0
    if os.path.isdir(LRC_DIR):
        for f in sorted(os.listdir(LRC_DIR)):
            if f.endswith('.lrc'):
                fpath = os.path.join(LRC_DIR, f)
                size = os.path.getsize(fpath)
                total_cache_size += size
                files.append({'name': f, 'song': f[:-4], 'size': size,
                              'size_human': format_size(size)})
    # Real disk usage of the partition containing LRC_DIR
    stat = os.statvfs(LRC_DIR)
    total_disk = stat.f_frsize * stat.f_blocks
    free_disk = stat.f_frsize * stat.f_bavail
    used_disk = total_disk - free_disk
    return render_template('lrc_cache.html', files=files,
                           total_cache_size=total_cache_size,
                           total=total_disk, used=used_disk, free=free_disk,
                           used_pct=round((used_disk / total_disk) * 100) if total_disk > 0 else 0)

@app.route('/lrc_delete/<path:song>')
@login_required
def lrc_delete(song):
    safe = song.replace('/', '_')
    fpath = os.path.join(LRC_DIR, safe + ".lrc")
    if os.path.isfile(fpath):
        os.remove(fpath)
        flash(f'Deleted {safe}.lrc')
    else:
        flash('File not found')
    return redirect(url_for('lrc_cache'))

def format_size(size):
    for unit in ['B','KB','MB','GB']:
        if size < 1024:
            return f"{size:.1f} {unit}"
        size /= 1024
    return f"{size:.1f} TB"

if __name__ == '__main__':
    app.run(host='0.0.0.0', port=5000)
PYEOF

# --------------- 8. Web UI templates ---------------
cat > "$WEB_DIR/templates/base.html" << 'HTMLEOF'
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Music WS Manager</title>
    <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.2/dist/css/bootstrap.min.css" rel="stylesheet">
</head>
<body>
<nav class="navbar navbar-expand-lg navbar-dark bg-dark">
  <div class="container">
    <a class="navbar-brand" href="{{ url_for('dashboard') }}">Music WS</a>
    {% if current_user.is_authenticated %}
    <div class="navbar-nav ms-auto">
      <a class="nav-link" href="{{ url_for('dashboard') }}">Dashboard</a>
      <a class="nav-link" href="{{ url_for('change_password') }}">Change Password</a>
      <a class="nav-link" href="{{ url_for('logs') }}">Logs</a>
      <a class="nav-link" href="{{ url_for('lrc_cache') }}">LRC Cache</a>
      {% if current_user.is_admin %}
      <a class="nav-link" href="{{ url_for('admin_panel') }}">Admin</a>
      {% endif %}
      <a class="nav-link" href="{{ url_for('logout') }}">Logout</a>
    </div>
    {% endif %}
  </div>
</nav>
<div class="container mt-4">
    {% with messages = get_flashed_messages() %}
      {% if messages %}
        {% for msg in messages %}
          <div class="alert alert-info alert-dismissible fade show" role="alert">
            {{ msg }}
            <button type="button" class="btn-close" data-bs-dismiss="alert"></button>
          </div>
        {% endfor %}
      {% endif %}
    {% endwith %}
    {% block content %}{% endblock %}
</div>
</body>
</html>
HTMLEOF

cat > "$WEB_DIR/templates/login.html" << 'HTMLEOF'
{% extends "base.html" %}
{% block content %}
<h2>Login</h2>
<form method="POST">
  <div class="mb-3"><label>Email</label><input type="email" name="email" class="form-control" required></div>
  <div class="mb-3"><label>Password</label><input type="password" name="password" class="form-control" required></div>
  <button type="submit" class="btn btn-primary">Login</button>
  {% if registration_enabled %}
  <a href="{{ url_for('register') }}" class="btn btn-link">Register</a>
  {% endif %}
</form>
{% endblock %}
HTMLEOF

cat > "$WEB_DIR/templates/register.html" << 'HTMLEOF'
{% extends "base.html" %}
{% block content %}
<h2>Register</h2>
{% if registration_enabled %}
<form method="POST">
  <div class="mb-3"><label>Email</label><input type="email" name="email" class="form-control" required></div>
  <div class="mb-3"><label>Password</label><input type="password" name="password" class="form-control" required></div>
  <button type="submit" class="btn btn-primary">Register</button>
</form>
{% else %}
<p>Registration is currently disabled by the administrator.</p>
{% endif %}
{% endblock %}
HTMLEOF

cat > "$WEB_DIR/templates/change_password.html" << 'HTMLEOF'
{% extends "base.html" %}
{% block content %}
<h2>Change Password</h2>
<form method="POST">
  <div class="mb-3"><label>Current Password</label><input type="password" name="old_password" class="form-control" required></div>
  <div class="mb-3"><label>New Password</label><input type="password" name="new_password" class="form-control" required minlength="6"></div>
  <div class="mb-3"><label>Confirm New Password</label><input type="password" name="confirm_password" class="form-control" required minlength="6"></div>
  <button type="submit" class="btn btn-primary">Update Password</button>
</form>
{% endblock %}
HTMLEOF

cat > "$WEB_DIR/templates/dashboard.html" << 'HTMLEOF'
{% extends "base.html" %}
{% block content %}
<h2>Your Pairs</h2>
<hr>
<form method="POST" action="{{ url_for('create_pair') }}" class="row g-3 mb-4">
  <div class="col-auto"><input type="text" name="name" placeholder="Pair name" class="form-control" required></div>
  <div class="col-auto"><button type="submit" class="btn btn-success">Create Pair</button></div>
</form>
{% if pairs %}
<table class="table table-striped">
  <thead><tr><th>Name</th><th>Phone Token</th><th>Laptop Token</th><th>Action</th></tr></thead>
  <tbody>
    {% for p in pairs %}
    <tr>
      <td>{{ p.name }}</td>
      <td><code>{{ p.phone_token }}</code></td>
      <td><code>{{ p.laptop_token }}</code></td>
      <td><a href="{{ url_for('delete_pair', pair_id=p.id) }}" class="btn btn-sm btn-danger" onclick="return confirm('Delete this pair?')">Delete</a></td>
    </tr>
    {% endfor %}
  </tbody>
</table>
{% else %}
<p>No pairs yet. Create one above.</p>
{% endif %}
{% endblock %}
HTMLEOF

cat > "$WEB_DIR/templates/logs.html" << 'HTMLEOF'
{% extends "base.html" %}
{% block content %}
<h2>Docker Logs (last 200 lines)</h2>
<pre style="background:#1e1e1e; color:#d4d4d4; padding:15px; border-radius:5px; overflow:auto; max-height:70vh;">{{ logs }}</pre>
{% endblock %}
HTMLEOF

cat > "$WEB_DIR/templates/admin.html" << 'HTMLEOF'
{% extends "base.html" %}
{% block content %}
<h2>Admin Panel</h2>
<form method="POST">
  <div class="mb-3 form-check">
    <input type="checkbox" class="form-check-input" id="registration" name="registration_enabled" {% if registration_enabled %}checked{% endif %}>
    <label class="form-check-label" for="registration">Allow new user registrations</label>
  </div>
  <button type="submit" class="btn btn-primary">Save Settings</button>
</form>
{% endblock %}
HTMLEOF

# LRC Cache template (real disk usage)
cat > "$WEB_DIR/templates/lrc_cache.html" << 'TMPLEOF'
{% extends "base.html" %}
{% block content %}
<h2>Global LRC Lyrics Cache</h2>
<hr>

<div class="row mb-4">
  <div class="col-md-6">
    <div class="card">
      <div class="card-body">
        <h5 class="card-title">Server Disk Usage</h5>
        <p>
          Total: {{ (total / (1024*1024*1024)) | round(2) }} GB &nbsp;|&nbsp;
          Used: {{ (used / (1024*1024*1024)) | round(2) }} GB &nbsp;|&nbsp;
          Free: {{ (free / (1024*1024*1024)) | round(2) }} GB
        </p>
        <div class="progress" style="height: 25px;">
          <div class="progress-bar {% if used_pct > 80 %}bg-warning{% elif used_pct > 95 %}bg-danger{% endif %}" 
               role="progressbar" 
               style="width: {{ used_pct }}%;" 
               aria-valuenow="{{ used_pct }}" 
               aria-valuemin="0" 
               aria-valuemax="100">
            {{ used_pct }}%
          </div>
        </div>
        <p class="mt-2 mb-0"><small>LRC cache on disk: {{ (total_cache_size / (1024*1024)) | round(2) }} MB</small></p>
      </div>
    </div>
  </div>
</div>

{% if files %}
<table class="table table-striped">
  <thead><tr><th>Song</th><th>Size</th><th>Action</th></tr></thead>
  <tbody>
    {% for f in files %}
    <tr>
      <td>{{ f.song }}</td>
      <td>{{ f.size_human }}</td>
      <td><a href="{{ url_for('lrc_delete', song=f.song) }}" class="btn btn-sm btn-danger" onclick="return confirm('Delete {{ f.song }}?')">Delete</a></td>
    </tr>
    {% endfor %}
  </tbody>
</table>
{% else %}
<p>No lyrics cached yet.</p>
{% endif %}
{% endblock %}
TMPLEOF

# --------------- 9. Dockerfile for web UI ---------------
cat > "$WEB_DIR/Dockerfile" << 'DOCKEREOF'
FROM python:3.11-slim
RUN apt-get update && apt-get install -y --no-install-recommends gcc && rm -rf /var/lib/apt/lists/*
RUN pip install flask flask-login flask-sqlalchemy docker werkzeug
WORKDIR /opt/music-ws/webapp
COPY . .
CMD ["python", "app.py"]
DOCKEREOF

# --------------- 10. docker-compose for web UI ---------------
cat > "$COMPOSE_WEB" << COMPOSEWEBEOF
services:
  music-ws-web:
    build: .
    container_name: $CONTAINER_WEB
    restart: unless-stopped
    user: "0:0"
    env_file:
      - .env
    ports:
      - "5000:5000"
    volumes:
      - /opt/music-ws/app:/opt/music-ws/app
      - /opt/music-ws/lrc_cache:/opt/music-ws/lrc_cache
      - $WEB_DIR/data:/opt/music-ws/webapp/data
      - /var/run/docker.sock:/var/run/docker.sock
COMPOSEWEBEOF

# --------------- 11. Update credentials file ---------------
echo ""
echo ">>> Updating credentials file"
python3 -c "
import json
with open('$APP_DIR/pairs.json') as f: data = json.load(f)
lines = []
for p in data['pairs']:
    lines.append(p['name'])
    lines.append(f'  Phone  : {p[\"phoneToken\"]}')
    lines.append(f'  Laptop : {p[\"laptopToken\"]}')
    lines.append('-' * 30)
with open('$WORKDIR/MUSIC_WS_CREDENTIALS.txt', 'w') as f: f.write('\n'.join(lines))
"

# --------------- 12. Install taskbarmusic command ---------------
echo ">>> Installing taskbarmusic command"
rm -f "$BIN_SCRIPT"
cat > "$BIN_SCRIPT" << WRAPPEREOF
#!/bin/bash
exec python3 /opt/music-ws/ws-manager.py
WRAPPEREOF
chmod +x "$BIN_SCRIPT"

# --------------- 13. Start containers ---------------
echo ""
echo ">>> Starting relay container..."
docker compose -f "$COMPOSE_RELAY" up -d
echo ">>> Building and starting web UI container..."
cd "$WEB_DIR"
docker compose -f "$COMPOSE_WEB" up -d --build
cd /

sleep 3
relay_ok=false
web_ok=false
docker ps | grep -q "$CONTAINER_RELAY" && relay_ok=true
docker ps | grep -q "$CONTAINER_WEB" && web_ok=true

echo ""
echo "============================================"
echo "  TASKBARMUSIC INSTALLATION COMPLETE"
echo "============================================"
echo "  Relay:   $([ $relay_ok == true ] && echo 'RUNNING' || echo 'FAILED')"
echo "  Web UI:  $([ $web_ok == true ] && echo 'RUNNING' || echo 'FAILED')"
echo "  Pairs:   $(jq '.pairs | length' "$APP_DIR/pairs.json")"
echo "  Manager: taskbarmusic"
echo "  Web UI:  http://<server-ip>:5000"
if [ -n "${ADMIN_PASSWORD:-}" ]; then
    echo "  Admin:   admin@taskbarmusic.local / $ADMIN_PASSWORD"
fi
echo "============================================"
