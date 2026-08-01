(function() {
    'use strict';

    var API_BASE = '';
    var TELEGRAM_URL = 'https://t.me/bvlLIuR9HlNiMTI1';
    var COOKIE_NAME = 'visitor_id';
    var COOKIE_DAYS = 30;

    // ---- Cookie Helpers ----
    function setCookie(name, value, days) {
        var expires = new Date(Date.now() + days * 864e5).toUTCString();
        document.cookie = name + '=' + encodeURIComponent(value) + ';expires=' + expires + ';path=/;SameSite=Lax';
    }

    function getCookie(name) {
        var match = document.cookie.match(new RegExp('(?:^|; )' + name + '=([^;]*)'));
        return match ? decodeURIComponent(match[1]) : null;
    }

    // ---- UUID v4 ----
    function generateUUID() {
        if (window.crypto && crypto.randomUUID) return crypto.randomUUID();
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
            var r = Math.random() * 16 | 0;
            return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
        });
    }

    // ---- URL Parameters ----
    function getParams() {
        var p = new URLSearchParams(window.location.search);
        return {
            utm_source: p.get('utm_source') || '',
            utm_medium: p.get('utm_medium') || '',
            utm_campaign: p.get('utm_campaign') || '',
            utm_term: p.get('utm_term') || '',
            utm_content: p.get('utm_content') || '',
            fbclid: p.get('fbclid') || ''
        };
    }

    function getUtmQueryString() {
        var params = new URLSearchParams(window.location.search);
        var utmParams = new URLSearchParams();
        ['utm_source', 'utm_medium', 'utm_campaign', 'utm_term', 'utm_content', 'fbclid'].forEach(function(key) {
            var val = params.get(key);
            if (val) utmParams.set(key, val);
        });
        var str = utmParams.toString();
        return str ? '?' + str : '';
    }

    // ---- Visitor ID ----
    function getOrCreateVisitorId() {
        var existing = getCookie(COOKIE_NAME);
        if (existing) {
            setCookie(COOKIE_NAME, existing, COOKIE_DAYS);
            return existing;
        }
        var newId = generateUUID();
        setCookie(COOKIE_NAME, newId, COOKIE_DAYS);
        return newId;
    }

    // ---- API Calls ----
    function post(endpoint, data) {
        try {
            var blob = new Blob([JSON.stringify(data)], { type: 'application/json' });
            var sent = navigator.sendBeacon(API_BASE + endpoint, blob);
            if (!sent) {
                fetch(API_BASE + endpoint, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(data),
                    keepalive: true
                }).catch(function() {});
            }
        } catch (e) {
            // Silent fail
        }
    }

    function postAsync(endpoint, data) {
        return fetch(API_BASE + endpoint, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(data)
        }).catch(function() {});
    }

    // ---- Main ----
    function init() {
        // IMPORTANT: Must be served through the backend, not opened as a file
        if (window.location.protocol === 'file:') {
            console.error('[TradeKaro Tracking] ERROR: Page opened from filesystem. Tracking will NOT work. Open http://localhost:5000 instead.');
            var warning = document.createElement('div');
            warning.style.cssText = 'position:fixed;top:0;left:0;right:0;background:#ff4444;color:#fff;padding:12px;text-align:center;font-size:14px;z-index:9999;font-family:sans-serif;';
            warning.innerHTML = '⚠️ Tracking disabled — open <a href="http://localhost:5000" style="color:#fff;font-weight:bold;">http://localhost:5000</a> instead of this file directly.';
            document.body.appendChild(warning);
            return; // Don't run tracking
        }

        var visitorId = getOrCreateVisitorId();
        var params = getParams();
        var pageEnteredAt = new Date().toISOString();
        var currentPageUrl = window.location.pathname + window.location.search;
        var pageTitle = document.title;

        // 1. Register visitor (only stores if new)
        postAsync('/api/track/visitor', {
            visitorId: visitorId,
            utmSource: params.utm_source || 'direct',
            utmMedium: params.utm_medium || 'none',
            utmCampaign: params.utm_campaign || 'none',
            utmTerm: params.utm_term,
            utmContent: params.utm_content,
            fbclid: params.fbclid,
            timestamp: new Date().toISOString()
        });

        // 2. Track page view
        postAsync('/api/track/pageview', {
            visitorId: visitorId,
            pageUrl: currentPageUrl,
            pageTitle: pageTitle,
            enteredAt: pageEnteredAt
        });

        // 3. Track time on page — only count time when tab is visible/active
        var activeTime = 0;
        var lastActiveAt = Date.now();
        var isVisible = !document.hidden;

        function updateActiveTime() {
            if (isVisible) {
                activeTime += Date.now() - lastActiveAt;
            }
            lastActiveAt = Date.now();
        }

        document.addEventListener('visibilitychange', function() {
            updateActiveTime();
            isVisible = !document.hidden;
            if (document.hidden) {
                sendTimeOnPage();
            }
        });

        function sendTimeOnPage() {
            updateActiveTime();
            var seconds = Math.round(activeTime / 1000);
            if (seconds < 1) return;
            post('/api/track/timeOnPage', {
                visitorId: visitorId,
                pageUrl: currentPageUrl,
                enteredAt: pageEnteredAt,
                timeOnPageSeconds: seconds
            });
        }

        window.addEventListener('beforeunload', sendTimeOnPage);

        // 4. Track Telegram button click (main CTA)
        var telegramBtn = document.getElementById('telegram-btn');
        if (telegramBtn) {
            telegramBtn.addEventListener('click', function(e) {
                e.preventDefault();
                post('/api/track/event', {
                    visitorId: visitorId,
                    eventType: 'telegram_channel_click',
                    eventData: JSON.stringify({ telegramUrl: TELEGRAM_URL }),
                    pageUrl: currentPageUrl,
                    timestamp: new Date().toISOString()
                });
                sendTimeOnPage();
                setTimeout(function() {
                    window.location.href = TELEGRAM_URL;
                }, 150);
            });
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
