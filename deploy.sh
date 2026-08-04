#!/usr/bin/env bash
set -Eeuo pipefail

# Reuse this script for the Telugu/Tamil-style deployments by changing only:
# - REPO_URL
# - APP_NAME
# - DOMAIN
# The rest of the deployment stays identical unless you override the optional variables.

REPO_URL=https://github.com/Aamir1055/AdsTrackingKannada 
APP_NAME=AdsTrackingKannada 
DOMAIN=telegram.kannada.tradekaro.com 
PORT=5004 
SOURCE_SERVICE=adstracking-tamil 
CERTBOT_EMAIL=your-email@domain.com 
DB_CONNECTION_STRING="${DB_CONNECTION_STRING:-}"
SKIP_CERTBOT="${SKIP_CERTBOT:-0}"

REPO_DIR="/root/${APP_NAME}"
PUBLISH_DIR="/var/www/${APP_NAME}"
SLUG="$(printf '%s' "${APP_NAME}" | tr '[:upper:]' '[:lower:]' | sed -E 's/^adstracking//; s/[^a-z0-9]+/-/g; s/^-+|-+$//g')"
SERVICE_NAME="${SERVICE_NAME:-adstracking-${SLUG:-site}}"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
NGINX_SITE="/etc/nginx/sites-available/${DOMAIN}"
NGINX_LINK="/etc/nginx/sites-enabled/${DOMAIN}"

require_root() {
  if [[ ${EUID} -ne 0 ]]; then
    echo "Run this script as root."
    exit 1
  fi
}

need_cmd() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "Missing required command: $1"
    exit 1
  }
}

repo_branch() {
  git -C "$REPO_DIR" rev-parse --abbrev-ref HEAD 2>/dev/null || echo "main"
}

clone_or_update_repo() {
  if [[ -d "$REPO_DIR/.git" ]]; then
    git -C "$REPO_DIR" remote set-url origin "$REPO_URL"
    git -C "$REPO_DIR" fetch origin --prune
    local branch
    branch="$(repo_branch)"
    git -C "$REPO_DIR" checkout "$branch" >/dev/null 2>&1 || true
    git -C "$REPO_DIR" pull --ff-only origin "$branch"
  else
    git clone "$REPO_URL" "$REPO_DIR"
  fi
}

publish_app() {
  rm -rf "$PUBLISH_DIR"
  mkdir -p "$PUBLISH_DIR"
  dotnet publish "$REPO_DIR/backend/AdsTracking.Api.csproj" -c Release -o "$PUBLISH_DIR"
}

service_env_lines() {
  if [[ -n "$SOURCE_SERVICE" && -f "/etc/systemd/system/${SOURCE_SERVICE}.service" ]]; then
    grep -E '^[[:space:]]*Environment=' "/etc/systemd/system/${SOURCE_SERVICE}.service" || true
    return
  fi

  cat <<EOF
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_ENVIRONMENT=Production
EOF

  if [[ -n "$DB_CONNECTION_STRING" ]]; then
    printf 'Environment=ConnectionStrings__DefaultConnection=%s\n' "$DB_CONNECTION_STRING"
  fi
}

write_systemd_service() {
  local env_block
  env_block="$(service_env_lines)"

  cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=Ads Tracking (${APP_NAME})
After=network.target

[Service]
WorkingDirectory=${PUBLISH_DIR}
ExecStart=/usr/bin/dotnet ${PUBLISH_DIR}/AdsTracking.Api.dll
Restart=always
RestartSec=5
SyslogIdentifier=${SERVICE_NAME}
User=root
$(printf '%s\n' "$env_block")
Environment=ASPNETCORE_URLS=http://127.0.0.1:${PORT}

[Install]
WantedBy=multi-user.target
EOF
}

write_nginx_config() {
  cat > "$NGINX_SITE" <<EOF
server {
    listen 80;
    server_name ${DOMAIN};

    location / {
        proxy_pass http://127.0.0.1:${PORT};
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_redirect off;
    }
}
EOF

  ln -sfn "$NGINX_SITE" "$NGINX_LINK"
}

enable_https() {
  if [[ "$SKIP_CERTBOT" == "1" ]]; then
    echo "SKIP_CERTBOT=1 set; skipping HTTPS automation."
    return
  fi

  if ! getent ahosts "$DOMAIN" >/dev/null 2>&1; then
    echo "DNS for $DOMAIN is not resolving yet; skipping Certbot for now."
    echo "Add an A/AAAA record for the domain, then rerun only the HTTPS step later."
    return
  fi

  if command -v certbot >/dev/null 2>&1; then
    if ! certbot --nginx -d "$DOMAIN" --non-interactive --agree-tos -m "$CERTBOT_EMAIL" --redirect; then
      echo "Certbot failed; the app and nginx deployment are still complete."
      echo "Fix DNS for $DOMAIN and rerun the script, or set SKIP_CERTBOT=1 until DNS is ready."
    fi
  else
    echo "certbot not installed; skipping HTTPS automation."
  fi
}

main() {
  require_root
  need_cmd git
  need_cmd dotnet
  need_cmd nginx

  clone_or_update_repo
  publish_app
  write_systemd_service
  write_nginx_config

  systemctl daemon-reload
  systemctl enable "$SERVICE_NAME"
  systemctl restart "$SERVICE_NAME"

  nginx -t
  systemctl reload nginx

  enable_https

  echo "Deployment complete."
  echo "Service: $SERVICE_NAME"
  echo "App folder: $PUBLISH_DIR"
  echo "Domain: https://$DOMAIN"
}

main "$@"