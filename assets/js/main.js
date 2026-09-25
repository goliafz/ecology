(function () {
  'use strict';

  // Тень у шапки при прокрутке
  var header = document.querySelector('.header');
  var onScroll = function () {
    header.classList.toggle('is-scrolled', window.scrollY > 8);
  };
  window.addEventListener('scroll', onScroll, { passive: true });
  onScroll();

  // Мобильное меню
  var burger = document.querySelector('.burger');
  var nav = document.getElementById('nav');
  var setMenu = function (open) {
    nav.classList.toggle('is-open', open);
    burger.setAttribute('aria-expanded', String(open));
    burger.setAttribute('aria-label', open ? 'Закрыть меню' : 'Открыть меню');
  };
  burger.addEventListener('click', function () {
    setMenu(!nav.classList.contains('is-open'));
  });
  nav.addEventListener('click', function (e) {
    if (e.target.closest('a')) setMenu(false);
  });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') setMenu(false);
  });

  // Вкладки «Жителям / Организациям»
  document.querySelectorAll('[data-tabs]').forEach(function (root) {
    var tabs = Array.prototype.slice.call(root.querySelectorAll('[role="tab"]'));
    var select = function (tab) {
      tabs.forEach(function (t) {
        var active = t === tab;
        t.classList.toggle('is-active', active);
        t.setAttribute('aria-selected', String(active));
        t.tabIndex = active ? 0 : -1;
        document.getElementById(t.getAttribute('aria-controls')).hidden = !active;
      });
    };
    tabs.forEach(function (tab, i) {
      tab.addEventListener('click', function () { select(tab); });
      tab.addEventListener('keydown', function (e) {
        if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
        var next = tabs[(i + (e.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length];
        select(next);
        next.focus();
      });
    });
  });

  // Ссылки «Физлицам» / «Юрлицам» открывают нужную вкладку
  document.addEventListener('click', function (e) {
    var link = e.target.closest('[data-tab]');
    if (link) {
      var tab = document.getElementById(link.getAttribute('data-tab'));
      if (tab) tab.click();
    }
  });

  // Плавное появление блоков
  var revealTargets = document.querySelectorAll('.section__head, .about__item, .card, .step, .contract, .ways li, .rule, .faq__item, .news__item, .form');
  if ('IntersectionObserver' in window) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (entry.isIntersecting) {
          entry.target.classList.add('is-visible');
          io.unobserve(entry.target);
        }
      });
    }, { rootMargin: '0px 0px -8% 0px' });
    revealTargets.forEach(function (el) {
      el.classList.add('reveal');
      io.observe(el);
    });
  }

  // Форма обращения: пока нет серверной части — открываем письмо с заполненным текстом
  var form = document.getElementById('feedback');
  var status = form.querySelector('.form__status');
  form.addEventListener('submit', function (e) {
    e.preventDefault();
    var invalid = false;
    form.querySelectorAll('[required]').forEach(function (field) {
      var bad = field.type === 'checkbox' ? !field.checked : !field.value.trim();
      field.classList.toggle('is-invalid', bad);
      if (bad) invalid = true;
    });
    if (invalid) {
      status.className = 'form__status is-error';
      status.textContent = 'Пожалуйста, заполните все поля и подтвердите согласие.';
      return;
    }
    var data = new FormData(form);
    var body = [
      'Тема: ' + data.get('topic'),
      'Имя: ' + data.get('name'),
      'Телефон: ' + data.get('phone'),
      'E-mail: ' + (data.get('email') || '—'),
      '',
      data.get('message')
    ].join('\n');
    window.location.href = 'mailto:info@eco-service27.ru'
      + '?subject=' + encodeURIComponent('Обращение с сайта: ' + data.get('topic'))
      + '&body=' + encodeURIComponent(body);
    status.className = 'form__status is-ok';
    status.textContent = 'Спасибо! Откроется почтовая программа — осталось нажать «Отправить».';
  });
  // Кнопки с темой заранее выбирают её в форме
  document.addEventListener('click', function (e) {
    var link = e.target.closest('[data-topic]');
    if (link) form.elements.topic.value = link.getAttribute('data-topic');
  });
  form.addEventListener('input', function (e) {
    e.target.classList.remove('is-invalid');
  });

  // Текущий год в подвале
  var year = document.querySelector('[data-year]');
  if (year) year.textContent = new Date().getFullYear();
})();
